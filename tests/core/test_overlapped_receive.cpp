#include <catch_amalgamated.hpp>

#include <rex/system/overlapped_receive.h>

#include <chrono>
#include <cstdint>
#include <deque>
#include <utility>
#include <vector>

using rex::system::net::OverlappedReceives;
using namespace std::chrono_literals;

namespace {
struct Request {
  uint32_t socket;
  int tag;
};

// A host socket: the datagrams waiting on it, by size.
struct FakeSocket {
  std::deque<int> datagrams;
  int attempts = 0;
  int operator()(const Request&) {
    ++attempts;
    if (datagrams.empty()) return -1;
    const int size = datagrams.front();
    datagrams.pop_front();
    return size;
  }
};

// A clock that only moves when the poll sleeps.
struct FakeTime {
  std::chrono::steady_clock::time_point now{};
  int sleeps = 0;
};

constexpr uint32_t kOverlapped = 0x7000A000;
}  // namespace

TEST_CASE("a waiting datagram completes the receive at once", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket{{120}};
  auto outcome = receives.Start(kOverlapped, {1, 0}, socket);
  CHECK(outcome.status == OverlappedReceives<Request>::Status::kCompleted);
  CHECK(outcome.bytes == 120);
  CHECK(receives.pending() == 0);
}

TEST_CASE("an overlapped receive with nothing waiting stays pending", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  auto outcome = receives.Start(kOverlapped, {1, 0}, socket);
  CHECK(outcome.status == OverlappedReceives<Request>::Status::kPending);
  CHECK(receives.pending() == 1);
}

TEST_CASE("a plain receive with nothing waiting would block", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  auto outcome = receives.Start(0, {1, 0}, socket);
  CHECK(outcome.status == OverlappedReceives<Request>::Status::kWouldBlock);
  CHECK(receives.pending() == 0);
}

TEST_CASE("polling completes a pending receive once its datagram arrives", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  FakeTime time;
  auto now = [&] { return time.now; };
  auto sleep = [&] { ++time.sleeps; time.now += 1ms; };
  receives.Start(kOverlapped, {1, 0}, socket);

  auto first = receives.Poll(kOverlapped, 0ms, socket, now, sleep);
  CHECK(first.status == OverlappedReceives<Request>::Status::kIncomplete);
  CHECK(receives.pending() == 1);

  socket.datagrams.push_back(64);
  auto second = receives.Poll(kOverlapped, 0ms, socket, now, sleep);
  CHECK(second.status == OverlappedReceives<Request>::Status::kCompleted);
  CHECK(second.bytes == 64);
  CHECK(receives.pending() == 0);
}

TEST_CASE("an overlapped with nothing outstanding reads its stored result", "[net]") {
  // A receive that completed at once, or a send (which always does).
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  FakeTime time;
  auto outcome =
      receives.Poll(kOverlapped, 5s, socket, [&] { return time.now; }, [&] { time.now += 1ms; });
  CHECK(outcome.status == OverlappedReceives<Request>::Status::kNotPending);
  CHECK(socket.attempts == 0);
}

TEST_CASE("a wait gives up as incomplete when no datagram comes", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  FakeTime time;
  receives.Start(kOverlapped, {1, 0}, socket);
  auto outcome = receives.Poll(kOverlapped, 5s, socket, [&] { return time.now; },
                               [&] { ++time.sleeps; time.now += 1ms; });
  CHECK(outcome.status == OverlappedReceives<Request>::Status::kIncomplete);
  CHECK(time.now - std::chrono::steady_clock::time_point{} >= 5s);
  CHECK(receives.pending() == 1);
}

TEST_CASE("a wait returns as soon as the datagram arrives", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  FakeTime time;
  receives.Start(kOverlapped, {1, 0}, socket);
  auto outcome = receives.Poll(kOverlapped, 5s, socket, [&] { return time.now; }, [&] {
    if (++time.sleeps == 3) socket.datagrams.push_back(32);
    time.now += 1ms;
  });
  CHECK(outcome.status == OverlappedReceives<Request>::Status::kCompleted);
  CHECK(outcome.bytes == 32);
  CHECK(time.sleeps == 3);
}

TEST_CASE("a new receive on the same overlapped replaces the pending one", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  receives.Start(kOverlapped, {1, 1}, socket);
  receives.Start(kOverlapped, {1, 2}, socket);
  CHECK(receives.pending() == 1);
  int seen_tag = 0;
  auto outcome = receives.Poll(
      kOverlapped, 0ms,
      [&](const Request& r) {
        seen_tag = r.tag;
        return 8;
      },
      [] { return std::chrono::steady_clock::time_point{}; }, [] {});
  CHECK(outcome.status == OverlappedReceives<Request>::Status::kCompleted);
  CHECK(seen_tag == 2);
}

TEST_CASE("closing a socket forgets only its pending receives", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket socket;
  receives.Start(0x100, {1, 0}, socket);
  receives.Start(0x200, {2, 0}, socket);
  receives.Forget(1);
  CHECK(receives.pending() == 1);
  FakeTime time;
  auto gone = receives.Poll(0x100, 0ms, socket, [&] { return time.now; }, [] {});
  CHECK(gone.status == OverlappedReceives<Request>::Status::kNotPending);
}

// WSAWaitForMultipleEvents -> NT wait (ticket online-latency 04).
using rex::system::net::EventWaitResult;
using rex::system::net::PlanEventWait;

TEST_CASE("a millisecond wait becomes a relative, non-zero NT timeout", "[net]") {
  const auto wait = PlanEventWait(/*wait_all=*/0, /*timeout_ms=*/10);
  REQUIRE(wait.timeout_ticks.has_value());
  CHECK(*wait.timeout_ticks == -100000);  // 10 ms in 100 ns units, negative = relative
}

TEST_CASE("an infinite event wait has no timeout, a zero one stays zero", "[net]") {
  CHECK_FALSE(PlanEventWait(0, 0xFFFFFFFF).timeout_ticks.has_value());
  CHECK(PlanEventWait(0, 0).timeout_ticks == 0);
}

TEST_CASE("fWaitAll FALSE waits for any event, TRUE for all", "[net]") {
  CHECK(PlanEventWait(0, 10).nt_wait_type == 1);  // KWAIT_TYPE WaitAny
  CHECK(PlanEventWait(1, 10).nt_wait_type == 0);  // KWAIT_TYPE WaitAll
}

TEST_CASE("wait any returns the index of the first signalled event", "[net]") {
  CHECK(EventWaitResult(/*nt_status=*/0, /*count=*/8) == 0);
  CHECK(EventWaitResult(3, 8) == 3);
}

TEST_CASE("an event wait that times out returns WSA_WAIT_TIMEOUT, not 0", "[net]") {
  CHECK(EventWaitResult(0x102, 8) == 0x102);
}

TEST_CASE("an alerted event wait returns WSA_WAIT_IO_COMPLETION", "[net]") {
  CHECK(EventWaitResult(0xC0, 8) == 0xC0);
}

TEST_CASE("a failed event wait returns WSA_WAIT_FAILED", "[net]") {
  CHECK(EventWaitResult(0xC000000D, 8) == 0xFFFFFFFF);  // STATUS_INVALID_PARAMETER
}

TEST_CASE("an arrival completes the pending receive of that socket only", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket nothing;
  receives.Start(0x100, {1, 0}, nothing);
  receives.Start(0x200, {2, 0}, nothing);
  CHECK(receives.Sockets() == std::vector<uint32_t>{1, 2});
  FakeSocket arrived{{64}};
  std::vector<std::pair<uint32_t, int>> completed;
  const int count = receives.Arrived(
      2, arrived, [&](uint32_t overlapped, int bytes) { completed.push_back({overlapped, bytes}); });
  CHECK(count == 1);
  CHECK(completed == std::vector<std::pair<uint32_t, int>>{{0x200, 64}});
  CHECK(receives.Sockets() == std::vector<uint32_t>{1});
  // The guest's own poll then finds the receive already finished.
  FakeTime time;
  auto later = receives.Poll(0x200, 0ms, nothing, [&] { return time.now; }, [] {});
  CHECK(later.status == OverlappedReceives<Request>::Status::kNotPending);
}

TEST_CASE("an arrival with no datagram left keeps the receive pending", "[net]") {
  OverlappedReceives<Request> receives;
  FakeSocket nothing;
  receives.Start(0x100, {1, 0}, nothing);
  int completed = 0;
  CHECK(receives.Arrived(1, nothing, [&](uint32_t, int) { ++completed; }) == 0);
  CHECK(completed == 0);
  CHECK(receives.pending() == 1);
}

TEST_CASE("a wake of the network thread returns WSA_WAIT_TIMEOUT so the title runs its pass",
          "[net]") {
  // The host's wake event sits after the title's 8 events.
  CHECK(EventWaitResult(8, 8, /*wake_slot=*/true) == 0x102);
  CHECK(EventWaitResult(2, 8, /*wake_slot=*/true) == 2);
  CHECK(EventWaitResult(8, 8) == 0xFFFFFFFF);  // no wake slot: out of range
}

TEST_CASE("only a blocking wait for any event takes the wake event", "[net]") {
  CHECK(PlanEventWait(0, 10).wakeable);
  CHECK(PlanEventWait(0, 0xFFFFFFFF).wakeable);
  CHECK_FALSE(PlanEventWait(0, 0).wakeable);   // a poll of the events' state
  CHECK_FALSE(PlanEventWait(1, 10).wakeable);  // fWaitAll: the wake would have to fire too
}
