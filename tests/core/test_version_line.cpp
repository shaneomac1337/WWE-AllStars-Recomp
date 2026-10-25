// The first runtime.log line (match-music ticket 01): which build wrote the log.

#include <catch_amalgamated.hpp>

#include "version_line.h"

using wwe::VersionLine;

TEST_CASE("the version line gives the release and the commit") {
  CHECK(VersionLine("1.6.1", "7631c78") == "WWE All Stars Recomp 1.6.1, commit 7631c78");
}

TEST_CASE("a build without a version or commit says unknown, as the crash manifest does") {
  CHECK(VersionLine("", "") == "WWE All Stars Recomp unknown, commit unknown");
}
