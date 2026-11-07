#include <catch_amalgamated.hpp>

#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#include "native/live_topup.h"
#include "native/shader_pack.h"

using wwe::native::live_topup::Item;
using wwe::native::live_topup::Queue;

namespace {

Item Shader(const std::string& name) { return {name, {1, 2, 3}, name.starts_with("ps_")}; }

}  // namespace

TEST_CASE("a shader the pack lacks is queued for conversion once") {
  Queue queue;
  CHECK(queue.Offer(Shader("ps_69d28119d5fe8475.bin")));
  CHECK_FALSE(queue.Offer(Shader("ps_69d28119d5fe8475.bin")));
  CHECK(queue.counts().queued == 1);

  const auto next = queue.Next();
  REQUIRE(next);
  CHECK(next->name == "ps_69d28119d5fe8475.bin");
  CHECK(next->is_pixel_shader);
  CHECK(next->container == std::vector<uint8_t>{1, 2, 3});
  CHECK_FALSE(queue.Next());
  // Being converted: still not queued again.
  CHECK_FALSE(queue.Offer(Shader("ps_69d28119d5fe8475.bin")));
}

TEST_CASE("shaders are converted in the order the game met them") {
  Queue queue;
  queue.Offer(Shader("vs_309cb027862dbde6.bin"));
  queue.Offer(Shader("ps_69d28119d5fe8475.bin"));
  CHECK(queue.Next()->name == "vs_309cb027862dbde6.bin");
  CHECK(queue.Next()->name == "ps_69d28119d5fe8475.bin");
}

TEST_CASE("a converted shader is never converted again in the session") {
  Queue queue;
  queue.Offer(Shader("ps_69d28119d5fe8475.bin"));
  queue.Finish(queue.Next()->name, /*converted=*/true);
  CHECK_FALSE(queue.Offer(Shader("ps_69d28119d5fe8475.bin")));
  CHECK_FALSE(queue.Next());
  CHECK(queue.counts().converted == 1);
  CHECK(queue.counts().queued == 0);
}

TEST_CASE("a failed conversion is not retried in the session") {
  Queue queue;
  queue.Offer(Shader("ps_69d28119d5fe8475.bin"));
  queue.Finish(queue.Next()->name, /*converted=*/false);
  CHECK_FALSE(queue.Offer(Shader("ps_69d28119d5fe8475.bin")));
  CHECK(queue.counts().failed == 1);
  CHECK(queue.counts().converted == 0);
}

namespace {

using wwe::native::Fallback;
using wwe::native::LoadShaderPack;
using wwe::native::live_topup::BuildPack;
using wwe::native::live_topup::ConverterOutput;
using wwe::native::live_topup::FetchSlotMask;

constexpr uint64_t kConverterId = 0x19bc9795a8b5e348ull;

// A zstd frame holding data in one raw block (as test_shader_pack.cpp).
std::vector<uint8_t> ZstdStored(const std::vector<uint8_t>& data) {
  std::vector<uint8_t> out = {0x28, 0xB5, 0x2F, 0xFD, 0xA0};
  for (int i = 0; i < 4; ++i) out.push_back(uint8_t(data.size() >> (8 * i)));
  const uint32_t block_header = 1u | (uint32_t(data.size()) << 3);
  out.push_back(uint8_t(block_header));
  out.push_back(uint8_t(block_header >> 8));
  out.push_back(uint8_t(block_header >> 16));
  out.insert(out.end(), data.begin(), data.end());
  return out;
}

// shader_cache.cpp as XenosRecomp writes it for one shader.
std::string ShaderCache(const std::vector<uint8_t>& spirv) {
  const std::vector<uint8_t> compressed = ZstdStored(spirv);
  std::string array;
  for (const uint8_t byte : compressed) array += std::to_string(byte) + ",";
  return "#include \"shader_cache.h\"\n"
         "ShaderCacheEntry g_shaderCacheEntries[] = {\n"
         "\t{ 0x133CF57390D27DA5, 0, 0, 0, " + std::to_string(spirv.size()) + ", 2 },\n"
         "};\n"
         "const uint8_t g_compressedSpirvCache[] = {" + array + "};\n"
         "const size_t g_spirvCacheCompressedSize = " + std::to_string(compressed.size()) + ";\n"
         "const size_t g_spirvCacheDecompressedSize = " + std::to_string(spirv.size()) + ";\n";
}

const char kSlotDefine[] =
    "#define sampDiffuse_Texture2DDescriptorIndex "
    "vk::RawBufferLoad<uint>(g_PushConstants.SharedConstants + 12)\n";

}  // namespace

TEST_CASE("a converted top-up becomes a one-shader pack the game loads") {
  const std::vector<uint8_t> spirv = {3, 2, 0x23, 7, 9, 9};
  const ConverterOutput output{ShaderCache(spirv),
                               {{"ps_69d28119d5fe8475.hlsl",
                                 std::string(kSlotDefine) +
                                     "float4 c = tex(sampDiffuse_Texture2DDescriptorIndex);\n"}}};
  const auto built = BuildPack(output, kConverterId);
  INFO(built.error);
  REQUIRE_FALSE(built.bytes.empty());

  const auto load = LoadShaderPack(built.bytes, kConverterId);
  INFO(load.detail);
  REQUIRE(load.fallback == Fallback::kNone);
  REQUIRE(load.pack.entries.size() == 1);
  CHECK(load.pack.entries[0].hash == 0x133CF57390D27DA5ull);
  CHECK(load.pack.entries[0].spirv_offset == 0);
  CHECK(load.pack.entries[0].spirv_size == 6);
  CHECK(load.pack.entries[0].spec_constants_mask == 2);
  REQUIRE(load.pack.fetch_slots.size() == 1);
  CHECK(load.pack.fetch_slots[0].fnv == 0x69d28119d5fe8475ull);
  CHECK(load.pack.fetch_slots[0].mask == 1u << 3);  // offset 12 / 4
  CHECK(load.pack.spirv == spirv);
}

TEST_CASE("output that is not XenosRecomp's shader cache builds no pack") {
  const auto built = BuildPack({"int main() {}", {}}, kConverterId);
  CHECK(built.bytes.empty());
  CHECK_FALSE(built.error.empty());
}

TEST_CASE("a shader cache whose array does not match its size builds no pack") {
  std::string text = ShaderCache({1, 2, 3});
  text.replace(text.find("CompressedSize = "), 18, "CompressedSize = 9");
  CHECK(BuildPack({text, {}}, kConverterId).bytes.empty());
}

TEST_CASE("a fetch slot without a define means every slot") {
  CHECK(FetchSlotMask(std::string(kSlotDefine) + "x = sampDiffuse_SamplerDescriptorIndex;\n") ==
        1u << 3);
  CHECK(FetchSlotMask("x = sampOther_Texture2DDescriptorIndex;\n") == UINT32_MAX);
  CHECK(FetchSlotMask("float4 main() { return 0; }\n") == 0);
}

namespace {

using wwe::native::MergeShaderPacks;
using wwe::native::ShaderPack;
using wwe::native::live_topup::ConvertedFileName;
using wwe::native::live_topup::LoadConverted;
using wwe::native::live_topup::SaveConverted;

namespace fs = std::filesystem;

struct TempFolder {
  fs::path path = fs::temp_directory_path() / "wwe_test_live_topup";
  TempFolder() { fs::remove_all(path); }
  ~TempFolder() { fs::remove_all(path); }
};

std::vector<uint8_t> OneShaderPack(uint64_t converter_id) {
  return BuildPack({ShaderCache({3, 2, 0x23, 7}), {{"ps_69d28119d5fe8475.hlsl", ""}}},
                   converter_id)
      .bytes;
}

}  // namespace

TEST_CASE("a converted top-up is saved beside its container, named after it") {
  CHECK(ConvertedFileName("ps_69d28119d5fe8475.bin") == "ps_69d28119d5fe8475.shaderpack");
  TempFolder folder;
  REQUIRE(SaveConverted(folder.path, "ps_69d28119d5fe8475.bin", OneShaderPack(kConverterId)));
  CHECK(fs::exists(folder.path / "ps_69d28119d5fe8475.shaderpack"));
  CHECK_FALSE(fs::exists(folder.path / "ps_69d28119d5fe8475.shaderpack.tmp"));
}

TEST_CASE("the next session loads the top-ups an earlier session converted") {
  TempFolder folder;
  SaveConverted(folder.path, "ps_69d28119d5fe8475.bin", OneShaderPack(kConverterId));
  std::vector<std::string> skipped;
  const std::vector<ShaderPack> packs = LoadConverted(folder.path, kConverterId, skipped);
  REQUIRE(packs.size() == 1);
  CHECK(packs[0].entries[0].hash == 0x133CF57390D27DA5ull);
  CHECK(skipped.empty());
}

TEST_CASE("a top-up converted with another XenosRecomp patch is not loaded") {
  TempFolder folder;
  SaveConverted(folder.path, "ps_69d28119d5fe8475.bin", OneShaderPack(0x9999));
  std::vector<std::string> skipped;
  CHECK(LoadConverted(folder.path, kConverterId, skipped).empty());
  CHECK(skipped.size() == 1);
}

TEST_CASE("no top-up folder loads nothing") {
  std::vector<std::string> skipped;
  CHECK(LoadConverted(fs::temp_directory_path() / "wwe_no_such_topups", kConverterId, skipped)
            .empty());
}

TEST_CASE("converted top-ups merge into the loaded pack") {
  ShaderPack base;
  base.converter_id = kConverterId;
  base.entries = {{0x1111, 0, 4, 2}, {0x2222, 4, 2, 0}};
  base.fetch_slots = {{0xA000, 1}, {0xC000, 2}};
  base.spirv = {1, 2, 3, 4, 5, 6};
  ShaderPack extra;
  extra.entries = {{0x1500, 0, 3, 5}};
  extra.fetch_slots = {{0xB000, 8}};
  extra.spirv = {7, 8, 9};
  ShaderPack duplicate;  // a shader the pack already holds
  duplicate.entries = {{0x2222, 0, 1, 0}};
  duplicate.fetch_slots = {{0xC000, 99}};
  duplicate.spirv = {42};

  const std::vector<ShaderPack> extras = {extra, duplicate};
  const ShaderPack merged = MergeShaderPacks(std::move(base), extras);
  REQUIRE(merged.entries.size() == 3);
  CHECK(merged.entries[0].hash == 0x1111);
  CHECK(merged.entries[1].hash == 0x1500);
  CHECK(merged.entries[1].spec_constants_mask == 5);
  CHECK(merged.entries[2].hash == 0x2222);
  CHECK(merged.entries[2].spirv_offset == 4);
  const auto& added = merged.entries[1];
  CHECK(std::vector<uint8_t>(merged.spirv.begin() + added.spirv_offset,
                             merged.spirv.begin() + added.spirv_offset + added.spirv_size) ==
        std::vector<uint8_t>{7, 8, 9});
  REQUIRE(merged.fetch_slots.size() == 3);
  CHECK(merged.fetch_slots[1].fnv == 0xB000);
  CHECK(merged.fetch_slots[2].mask == 2);
  CHECK(merged.converter_id == kConverterId);
}

namespace {

using wwe::native::live_topup::Converter;
using wwe::native::live_topup::FindConverter;

void Touch(const fs::path& path) {
  fs::create_directories(path.parent_path());
  std::ofstream(path) << "x";
}

}  // namespace

TEST_CASE("the game converts with the release's shader_common.h beside the game folder") {
  TempFolder root;
  Touch(root.path / "xenosrecomp" / "shader_common.h");
  const auto found = FindConverter(root.path / "game" / "wweallstars.exe", {}, {});
  REQUIRE(found);
  CHECK(found->executable == root.path / "game" / "wweallstars.exe");
  CHECK(found->include == root.path / "xenosrecomp" / "shader_common.h");
}

TEST_CASE("a named shader_common.h wins") {
  TempFolder root;
  Touch(root.path / "xenosrecomp" / "shader_common.h");
  Touch(root.path / "mine" / "shader_common.h");
  const auto found = FindConverter(root.path / "game" / "wweallstars.exe",
                                   root.path / "mine" / "shader_common.h", {});
  REQUIRE(found);
  CHECK(found->include == root.path / "mine" / "shader_common.h");
}

TEST_CASE("the dev build's shader_common.h is found when no release one is") {
  TempFolder root;
  Touch(root.path / "tools" / "shader_common.h");
  const auto found =
      FindConverter(root.path / "game" / "wweallstars.exe", {}, root.path / "tools" / "shader_common.h");
  REQUIRE(found);
  CHECK(found->include == root.path / "tools" / "shader_common.h");
}

// Opt-in check of real XenosRecomp output for one top-up (game-derived, so
// never in the repo): WWE_TEST_CONVERTER_OUTPUT=<folder with shader_cache.cpp
// and hlsl/> wweallstars_native_tests "[real_converter_output]".
TEST_CASE("real XenosRecomp output builds a pack that loads", "[.real_converter_output]") {
  const char* folder = std::getenv("WWE_TEST_CONVERTER_OUTPUT");
  if (!folder) SKIP("WWE_TEST_CONVERTER_OUTPUT is not set");
  const auto read = [](const fs::path& path) {
    std::ifstream file(path, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(file), std::istreambuf_iterator<char>());
  };
  ConverterOutput output{read(fs::path(folder) / "shader_cache.cpp"), {}};
  for (const auto& entry : fs::directory_iterator(fs::path(folder) / "hlsl")) {
    output.hlsl.emplace_back(entry.path().filename().string(), read(entry.path()));
  }
  const auto built = BuildPack(output, kConverterId);
  INFO(built.error);
  REQUIRE_FALSE(built.bytes.empty());
  const auto load = LoadShaderPack(built.bytes, kConverterId);
  INFO(load.detail);
  REQUIRE(load.fallback == Fallback::kNone);
  CHECK(load.pack.entries.size() == 1);
  CHECK(load.pack.fetch_slots.size() == 1);
}

TEST_CASE("without shader_common.h nothing is found") {
  TempFolder root;
  CHECK_FALSE(FindConverter(root.path / "game" / "wweallstars.exe", {}, {}));
}
