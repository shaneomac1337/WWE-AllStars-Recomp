#include "native/live_topup.h"

#include <algorithm>
#include <cctype>
#include <fstream>
#include <regex>

namespace wwe::native::live_topup {

bool Queue::Offer(Item item) {
  if (!state_.emplace(item.name, State::kQueued).second) {
    return false;
  }
  waiting_.push_back(std::move(item));
  return true;
}

std::optional<Item> Queue::Next() {
  if (waiting_.empty()) {
    return std::nullopt;
  }
  Item item = std::move(waiting_.front());
  waiting_.erase(waiting_.begin());
  state_[item.name] = State::kConverting;
  return item;
}

void Queue::Finish(const std::string& name, bool converted) {
  const auto it = state_.find(name);
  if (it != state_.end() && it->second == State::kConverting) {
    it->second = converted ? State::kConverted : State::kFailed;
  }
}

Counts Queue::counts() const {
  Counts counts;
  for (const auto& [name, state] : state_) {
    counts.queued += state == State::kQueued || state == State::kConverting;
    counts.converted += state == State::kConverted;
    counts.failed += state == State::kFailed;
  }
  return counts;
}

namespace {

constexpr uint32_t kSlots = 32;
constexpr char kConvertedExtension[] = ".shaderpack";
constexpr std::string_view kTable = "g_shaderCacheEntries[] = {";
constexpr std::string_view kArray = "g_compressedSpirvCache[] = {";

void Put32(std::vector<uint8_t>& out, uint32_t value) {
  for (int i = 0; i < 4; ++i) out.push_back(uint8_t(value >> (8 * i)));
}

void Put64(std::vector<uint8_t>& out, uint64_t value) {
  for (int i = 0; i < 8; ++i) out.push_back(uint8_t(value >> (8 * i)));
}

std::string_view Trim(std::string_view text) {
  while (!text.empty() && std::isspace(uint8_t(text.front()))) text.remove_prefix(1);
  while (!text.empty() && std::isspace(uint8_t(text.back()))) text.remove_suffix(1);
  return text;
}

std::vector<std::string_view> Lines(std::string_view text) {
  std::vector<std::string_view> lines;
  for (size_t start = 0; start < text.size();) {
    const size_t end = std::min(text.find('\n', start), text.size());
    lines.push_back(text.substr(start, end - start));
    start = end + 1;
  }
  return lines;
}

uint64_t ParseNumber(const std::string& text) {
  return text.starts_with("0x") || text.starts_with("0X")
             ? std::stoull(text.substr(2), nullptr, 16)
             : std::stoull(text);
}

}  // namespace

// The same patterns as the launcher's ShaderPackFile and scripts/shader_pack.py.
PackBuild BuildPack(const ConverterOutput& output, uint64_t converter_id) {
  const std::string& text = output.shader_cache;
  const size_t table_start = text.find(kTable);
  const size_t table_end =
      table_start == std::string::npos ? std::string::npos : text.find("\n};", table_start);
  const size_t array_start = text.find(kArray);
  const size_t array_end =
      array_start == std::string::npos ? std::string::npos : text.find('}', array_start);
  static const std::regex compressed_size(R"(\bg_spirvCacheCompressedSize\s*=\s*(\d+))");
  static const std::regex decompressed_size(R"(\bg_spirvCacheDecompressedSize\s*=\s*(\d+))");
  std::smatch compressed_match, decompressed_match;
  if (table_end == std::string::npos || array_end == std::string::npos ||
      !std::regex_search(text, compressed_match, compressed_size) ||
      !std::regex_search(text, decompressed_match, decompressed_size)) {
    return {{}, "XenosRecomp did not write a shader cache"};
  }

  struct Entry {
    uint64_t hash;
    uint32_t offset, size, mask;
  };
  // { hash, dxilOffset, dxilSize, spirvOffset, spirvSize, specConstantsMask }
  static const std::regex entry_pattern(
      R"(\{\s*(0x[0-9A-Fa-f]+|\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*\})");
  std::vector<Entry> entries;
  const std::string table = text.substr(table_start + kTable.size(), table_end - table_start);
  for (std::sregex_iterator it(table.begin(), table.end(), entry_pattern), end; it != end; ++it) {
    entries.push_back({ParseNumber((*it)[1]), uint32_t(std::stoul((*it)[4])),
                       uint32_t(std::stoul((*it)[5])), uint32_t(std::stoul((*it)[6]))});
  }
  std::sort(entries.begin(), entries.end(),
            [](const Entry& a, const Entry& b) { return a.hash < b.hash; });

  std::vector<uint8_t> compressed;
  const size_t values = array_start + kArray.size();
  std::string_view array(text.data() + values, array_end - values);
  while (!array.empty()) {
    const size_t comma = std::min(array.find(','), array.size());
    const std::string_view value = Trim(array.substr(0, comma));
    array.remove_prefix(std::min(comma + 1, array.size()));
    if (!value.empty()) {
      compressed.push_back(uint8_t(std::stoul(std::string(value))));
    }
  }
  if (compressed.size() != std::stoull(compressed_match[1])) {
    return {{}, "XenosRecomp's compressed SPIR-V does not match its size"};
  }
  if (entries.empty()) {
    return {{}, "XenosRecomp converted no shader"};
  }

  std::vector<std::pair<uint64_t, uint32_t>> slots;
  static const std::regex hlsl_name(R"(^(?:ps|vs)_([0-9a-f]{16})\.hlsl$)");
  for (const auto& [name, hlsl] : output.hlsl) {
    std::smatch match;
    if (std::regex_match(name, match, hlsl_name)) {
      slots.emplace_back(std::stoull(match[1], nullptr, 16), FetchSlotMask(hlsl));
    }
  }
  std::sort(slots.begin(), slots.end());

  PackBuild build;
  std::vector<uint8_t>& out = build.bytes;
  out = {'W', 'W', 'S', 'P'};
  Put32(out, 1);  // format version (kShaderPackVersion)
  Put64(out, converter_id);
  Put32(out, uint32_t(entries.size()));
  Put32(out, uint32_t(slots.size()));
  Put32(out, uint32_t(compressed.size()));
  Put32(out, uint32_t(std::stoul(decompressed_match[1])));
  for (const Entry& entry : entries) {
    Put64(out, entry.hash);
    Put32(out, entry.offset);
    Put32(out, entry.size);
    Put32(out, entry.mask);
    Put32(out, 0);
  }
  for (const auto& [fnv, mask] : slots) {
    Put64(out, fnv);
    Put32(out, mask);
    Put32(out, 0);
  }
  out.insert(out.end(), compressed.begin(), compressed.end());
  return build;
}

// XenosRecomp names each sampler slot with a define (offset / 4 = dimension *
// 32 + slot); the body refers to those names.
uint32_t FetchSlotMask(std::string_view hlsl) {
  static const std::regex define(
      R"(^#define (\w+)_(?:Texture\w+|Sampler)DescriptorIndex vk::RawBufferLoad<uint>\(g_PushConstants\.SharedConstants \+ (\d+)\))");
  static const std::regex reference(
      R"(\b(\w+)_(?:Texture(?:2D|3D|Cube)|Sampler)DescriptorIndex\b)");
  using Match = std::match_results<std::string_view::const_iterator>;
  using Iterator = std::regex_iterator<std::string_view::const_iterator>;
  const std::vector<std::string_view> lines = Lines(hlsl);
  std::unordered_map<std::string, uint32_t> slots;
  for (const std::string_view line : lines) {
    Match match;
    if (std::regex_search(line.begin(), line.end(), match, define)) {
      slots[match[1].str()] = uint32_t(std::stoul(match[2].str()) / 4 % kSlots);
    }
  }
  uint32_t mask = 0;
  for (const std::string_view line : lines) {
    if (Trim(line).starts_with("#define") || line.find("packoffset") != std::string_view::npos) {
      continue;
    }
    for (Iterator it(line.begin(), line.end(), reference), end; it != end; ++it) {
      const auto slot = slots.find((*it)[1].str());
      if (slot == slots.end()) {
        return UINT32_MAX;
      }
      mask |= 1u << slot->second;
    }
  }
  return mask;
}

std::string ConvertedFileName(std::string_view container_name) {
  std::string name(container_name);
  if (name.ends_with(".bin")) {
    name.resize(name.size() - 4);
  }
  return name + kConvertedExtension;
}

bool SaveConverted(const std::filesystem::path& folder, std::string_view container_name,
                   const std::vector<uint8_t>& pack) {
  std::error_code error;
  std::filesystem::create_directories(folder, error);
  const std::filesystem::path path = folder / ConvertedFileName(container_name);
  std::filesystem::path temp = path;
  temp += ".tmp";
  {
    std::ofstream file(temp, std::ios::binary | std::ios::trunc);
    if (!file.write(reinterpret_cast<const char*>(pack.data()), std::streamsize(pack.size()))) {
      file.close();
      std::filesystem::remove(temp, error);
      return false;
    }
  }
  std::filesystem::rename(temp, path, error);
  if (error) {
    std::filesystem::remove(temp, error);
    return false;
  }
  return true;
}

std::vector<ShaderPack> LoadConverted(const std::filesystem::path& folder,
                                      uint64_t expected_converter_id,
                                      std::vector<std::string>& skipped) {
  std::vector<std::filesystem::path> files;
  std::error_code error;
  for (std::filesystem::directory_iterator it(folder, error), end; !error && it != end;
       it.increment(error)) {
    if (it->path().extension() == kConvertedExtension) {
      files.push_back(it->path());
    }
  }
  std::sort(files.begin(), files.end());
  std::vector<ShaderPack> packs;
  for (const std::filesystem::path& file : files) {
    ShaderPackLoad load = LoadShaderPackFile(file, expected_converter_id);
    if (load.fallback == Fallback::kNone) {
      packs.push_back(std::move(load.pack));
    } else {
      skipped.push_back(file.filename().string() + " (" + FallbackName(load.fallback) + ")");
    }
  }
  return packs;
}

std::optional<Converter> FindConverter(const std::filesystem::path& game_executable,
                                       const std::filesystem::path& named_include,
                                       const std::filesystem::path& dev_include) {
  const std::filesystem::path release =
      game_executable.parent_path().parent_path() / "xenosrecomp" / "shader_common.h";
  for (const std::filesystem::path& include : {named_include, release, dev_include}) {
    std::error_code error;
    if (!include.empty() && std::filesystem::is_regular_file(include, error)) {
      return Converter{game_executable, include};
    }
  }
  return std::nullopt;
}

}  // namespace wwe::native::live_topup
