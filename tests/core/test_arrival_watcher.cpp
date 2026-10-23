// Ticket online-latency 04: a datagram landing on a host socket completes the pending
// overlapped receive and sets its event without the guest polling.
#include <winsock2.h>

#include <catch_amalgamated.hpp>

#include <rex/system/arrival_watcher.h>
#include <rex/system/overlapped_receive.h>
#include <rex/thread.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

using rex::system::net::ArrivalWatcher;
using rex::system::net::ArrivalWatcherOptions;
using rex::system::net::OverlappedReceives;
using namespace std::chrono_literals;

namespace {
struct Request {
  uint32_t socket;  // guest handle
  SOCKET native;
};

struct Winsock {
  Winsock() {
    WSADATA data;
    WSAStartup(MAKEWORD(2, 2), &data);
  }
  ~Winsock() { WSACleanup(); }
};

// A non-blocking UDP socket on 127.0.0.1 and an ephemeral port.
SOCKET LoopbackUdp(sockaddr_in& name) {
  SOCKET s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
  name = {};
  name.sin_family = AF_INET;
  name.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  bind(s, reinterpret_cast<sockaddr*>(&name), sizeof(name));
  int len = sizeof(name);
  getsockname(s, reinterpret_cast<sockaddr*>(&name), &len);
  u_long non_blocking = 1;
  ioctlsocket(s, FIONBIO, &non_blocking);
  return s;
}

// A guest event: set once, waited on with a deadline.
struct Event {
  std::mutex mutex;
  std::condition_variable cv;
  bool set = false;
  void Set() {
    std::lock_guard lock(mutex);
    set = true;
    cv.notify_all();
  }
  bool WaitFor(std::chrono::milliseconds limit) {
    std::unique_lock lock(mutex);
    return cv.wait_for(lock, limit, [this] { return set; });
  }
};

constexpr uint32_t kGuestSocket = 0x40;
constexpr uint32_t kOverlapped = 0x7000A000;
}  // namespace

TEST_CASE("a datagram sent to a socket with a pending receive sets its event and completes it",
          "[net]") {
  Winsock winsock;
  sockaddr_in name;
  const SOCKET receiver = LoopbackUdp(name);
  OverlappedReceives<Request> receives;
  auto receive = [](const Request& r) {
    char data[2048];
    const int n = recv(r.native, data, sizeof(data), 0);
    return n >= 0 ? n : -1;
  };
  Event event;
  uint32_t completed_overlapped = 0;
  int completed_bytes = -1;

  ArrivalWatcher watcher(
      [&] {
        std::vector<ArrivalWatcher::Watch> watched;
        for (uint32_t s : receives.Sockets()) {
          watched.push_back({uint64_t(receiver), s});
        }
        return watched;
      },
      [&](uint32_t socket) {
        return receives.Arrived(socket, receive, [&](uint32_t overlapped, int bytes) {
                 completed_overlapped = overlapped;
                 completed_bytes = bytes;
                 event.Set();
               }) > 0;
      });

  // The guest posts the receive after the watcher is running, then the datagram lands.
  REQUIRE(receives.Start(kOverlapped, {kGuestSocket, receiver}, receive).status ==
          OverlappedReceives<Request>::Status::kPending);
  watcher.Wake();
  sockaddr_in unused;
  const SOCKET sender = LoopbackUdp(unused);
  const char datagram[100] = {};
  const auto sent_at = std::chrono::steady_clock::now();
  sendto(sender, datagram, sizeof(datagram), 0, reinterpret_cast<sockaddr*>(&name), sizeof(name));

  REQUIRE(event.WaitFor(1000ms));
  // About today's receive latency, not a 10 ms poll.
  CHECK(std::chrono::steady_clock::now() - sent_at < 50ms);
  CHECK(completed_overlapped == kOverlapped);
  CHECK(completed_bytes == 100);
  CHECK(receives.pending() == 0);
  closesocket(sender);
  closesocket(receiver);
}

TEST_CASE("a watcher with nothing to watch blocks instead of spinning", "[net]") {
  Winsock winsock;
  std::atomic<int> passes{0};
  {
    ArrivalWatcher watcher(
        [&] {
          ++passes;
          return std::vector<ArrivalWatcher::Watch>{};
        },
        [](uint32_t) { return false; });
    std::this_thread::sleep_for(300ms);
  }
  CHECK(passes < 10);
}

TEST_CASE("a watcher without its wake socket polls, warns once, and still completes receives",
          "[net]") {
  Winsock winsock;
  sockaddr_in name;
  const SOCKET receiver = LoopbackUdp(name);
  OverlappedReceives<Request> receives;
  auto receive = [](const Request& r) {
    char data[2048];
    const int n = recv(r.native, data, sizeof(data), 0);
    return n >= 0 ? n : -1;
  };
  Event event;
  std::atomic<int> passes{0};
  std::vector<std::string> warnings;
  ArrivalWatcherOptions options;
  options.open_wake_socket = false;  // as when opening it fails
  options.warn = [&](const char* message) { warnings.push_back(message); };
  {
    ArrivalWatcher watcher(
        [&] {
          ++passes;
          std::vector<ArrivalWatcher::Watch> watched;
          for (uint32_t s : receives.Sockets()) {
            watched.push_back({uint64_t(receiver), s});
          }
          return watched;
        },
        [&](uint32_t socket) {
          return receives.Arrived(socket, receive, [&](uint32_t, int) { event.Set(); }) > 0;
        },
        options);
    std::this_thread::sleep_for(300ms);
    // A steady poll, not a spin on a failing select().
    CHECK(passes < 60);

    // Wake() can't reach it; the next poll picks the new receive up.
    REQUIRE(receives.Start(kOverlapped, {kGuestSocket, receiver}, receive).status ==
            OverlappedReceives<Request>::Status::kPending);
    watcher.Wake();
    sockaddr_in unused;
    const SOCKET sender = LoopbackUdp(unused);
    const char datagram[10] = {};
    sendto(sender, datagram, sizeof(datagram), 0, reinterpret_cast<sockaddr*>(&name),
           sizeof(name));
    CHECK(event.WaitFor(1000ms));
    CHECK(receives.pending() == 0);
    closesocket(sender);
  }
  CHECK(warnings.size() == 1);
  closesocket(receiver);
}

TEST_CASE("a socket that stays readable with nothing taken is backed off, not spun on", "[net]") {
  // A stale snapshot: the native handle now belongs to a socket with no pending receive
  // for this id, so every pass finds it readable and takes nothing.
  Winsock winsock;
  sockaddr_in name;
  const SOCKET receiver = LoopbackUdp(name);
  sockaddr_in unused;
  const SOCKET sender = LoopbackUdp(unused);
  const char datagram[10] = {};
  sendto(sender, datagram, sizeof(datagram), 0, reinterpret_cast<sockaddr*>(&name), sizeof(name));
  std::atomic<int> passes{0};
  {
    ArrivalWatcher watcher(
        [&] {
          ++passes;
          return std::vector<ArrivalWatcher::Watch>{{uint64_t(receiver), kGuestSocket}};
        },
        [](uint32_t) { return false; });
    std::this_thread::sleep_for(300ms);
  }
  CHECK(passes < 80);
  closesocket(sender);
  closesocket(receiver);
}

// WSAWaitForMultipleEvents on real host events: the plan's wait type and timeout reach the
// host wait as XObject::WaitMultiple passes them (wait_type 0 = WaitAll), and the index it
// returns is the NT status the result is read from.
TEST_CASE("a wait for any of several events returns the one that was set", "[net]") {
  using rex::system::net::EventWaitResult;
  using rex::system::net::PlanEventWait;
  using rex::thread::Event;
  constexpr uint32_t kTitleEvents = 4;
  std::vector<std::unique_ptr<Event>> events;
  std::vector<rex::thread::WaitHandle*> handles;
  for (uint32_t i = 0; i < kTitleEvents + 1; ++i) {  // + the host's wake slot
    events.push_back(Event::CreateAutoResetEvent(false));
    handles.push_back(events.back().get());
  }
  const auto host_wait = [&](uint32_t timeout_ms) {
    const auto plan = PlanEventWait(/*wait_all=*/0, timeout_ms);
    REQUIRE(plan.wakeable);
    return rex::thread::WaitMultiple(handles.data(), handles.size(),
                                     /*wait_all=*/plan.nt_wait_type == 0, false,
                                     std::chrono::milliseconds(-*plan.timeout_ticks / 10000));
  };
  const auto wsa_wait = [&] {
    const auto [result, index] = host_wait(1000);
    const uint32_t nt_status = result == rex::thread::WaitResult::kSuccess ? uint32_t(index) : 0x102u;
    return EventWaitResult(nt_status, kTitleEvents, /*wake_slot=*/true);
  };

  SECTION("one title event set") {
    events[2]->Set();
    const auto started = std::chrono::steady_clock::now();
    CHECK(wsa_wait() == 2);
    CHECK(std::chrono::steady_clock::now() - started < 500ms);  // did not wait for the others
  }
  SECTION("only the wake slot set") {
    events[kTitleEvents]->Set();
    CHECK(wsa_wait() == 0x102);  // WSA_WAIT_TIMEOUT: the title runs its pass
  }
  SECTION("none set") {
    CHECK(host_wait(20).first == rex::thread::WaitResult::kTimeout);
  }
}
