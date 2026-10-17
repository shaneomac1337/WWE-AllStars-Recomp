// A zip with stored (uncompressed) entries, written entry by entry so the
// crash path never holds the whole zip in memory, and with no compression
// library in it. The logs in a report are capped (crash_report_plan.h).

#pragma once

#include <array>
#include <cstdint>
#include <ctime>
#include <functional>
#include <string_view>
#include <utility>
#include <vector>

namespace wwe::crash {

inline uint32_t Crc32(const std::vector<uint8_t>& data) {
  static const std::array<uint32_t, 256> table = [] {
    std::array<uint32_t, 256> t{};
    for (uint32_t i = 0; i < 256; ++i) {
      uint32_t c = i;
      for (int k = 0; k < 8; ++k) c = (c & 1) ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      t[i] = c;
    }
    return t;
  }();
  uint32_t crc = 0xFFFFFFFFu;
  for (uint8_t byte : data) crc = table[(crc ^ byte) & 0xFF] ^ (crc >> 8);
  return crc ^ 0xFFFFFFFFu;
}

class StoredZipWriter {
 public:
  // `write` returns false when it could not write everything; every entry
  // gets `time` (local) as its date.
  StoredZipWriter(std::function<bool(const uint8_t*, size_t)> write, const std::tm& time)
      : write_(std::move(write)),
        dos_time_(uint16_t(time.tm_hour << 11 | time.tm_min << 5 | time.tm_sec / 2)),
        dos_date_(uint16_t((time.tm_year - 80) << 9 | (time.tm_mon + 1) << 5 | time.tm_mday)) {}

  // `name` is UTF-8. Returns false once any write failed.
  bool Add(std::string_view name, const std::vector<uint8_t>& data) {
    const uint32_t crc = Crc32(data);
    const uint32_t size = uint32_t(data.size());
    std::vector<uint8_t> header;
    U32(header, 0x04034B50);
    U16(header, 20); U16(header, kUtf8Names); U16(header, 0);
    U16(header, dos_time_); U16(header, dos_date_);
    U32(header, crc); U32(header, size); U32(header, size);
    U16(header, uint32_t(name.size())); U16(header, 0);
    header.insert(header.end(), name.begin(), name.end());

    U32(central_, 0x02014B50);
    U16(central_, 20); U16(central_, 20); U16(central_, kUtf8Names); U16(central_, 0);
    U16(central_, dos_time_); U16(central_, dos_date_);
    U32(central_, crc); U32(central_, size); U32(central_, size);
    U16(central_, uint32_t(name.size())); U16(central_, 0); U16(central_, 0); U16(central_, 0);
    U16(central_, 0); U32(central_, 0); U32(central_, offset_);
    central_.insert(central_.end(), name.begin(), name.end());
    ++count_;

    Write(header.data(), header.size());
    Write(data.data(), data.size());
    return ok_;
  }

  // Writes the central directory. Returns false if any write failed.
  bool Finish() {
    std::vector<uint8_t> end = central_;
    U32(end, 0x06054B50);
    U16(end, 0); U16(end, 0);
    U16(end, count_); U16(end, count_);
    U32(end, uint32_t(central_.size())); U32(end, offset_);
    U16(end, 0);
    Write(end.data(), end.size());
    return ok_;
  }

 private:
  static constexpr uint32_t kUtf8Names = 0x0800;  // general purpose bit 11

  static void U16(std::vector<uint8_t>& v, uint32_t x) {
    v.push_back(uint8_t(x));
    v.push_back(uint8_t(x >> 8));
  }
  static void U32(std::vector<uint8_t>& v, uint32_t x) {
    U16(v, x & 0xFFFF);
    U16(v, x >> 16);
  }

  void Write(const uint8_t* data, size_t size) {
    if (ok_ && size) ok_ = write_(data, size);
    offset_ += uint32_t(size);
  }

  std::function<bool(const uint8_t*, size_t)> write_;
  uint16_t dos_time_;
  uint16_t dos_date_;
  std::vector<uint8_t> central_;
  uint32_t offset_ = 0;
  uint32_t count_ = 0;
  bool ok_ = true;
};

}  // namespace wwe::crash
