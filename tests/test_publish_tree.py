import os
import subprocess
import tempfile
import unittest
from pathlib import Path

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "publish-tree.ps1"
NOREPLY = "shaneomac1337@users.noreply.github.com"


def git(repo, *args):
    return subprocess.run(["git", "-C", str(repo), *args], check=True, capture_output=True,
                          text=True).stdout.strip()


def publish(repo, message):
    # Isolated from the caller's identity and global git config.
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_")}
    env["GIT_CONFIG_GLOBAL"] = os.devnull
    return subprocess.run(["pwsh", "-NoProfile", "-File", str(SCRIPT), "-Message", message],
                          cwd=repo, capture_output=True, text=True, env=env)


def published(repo, message):
    result = publish(repo, message)
    assert result.returncode == 0, result.stderr
    return result


class PublishTreeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.repo = Path(self.tmp.name)
        git(self.repo, "init", "-q", "-b", "main")
        git(self.repo, "config", "user.name", "Private Name")
        git(self.repo, "config", "user.email", "private@example.com")
        self.write("README.md", "hello\n")
        self.write("docs/research/package-hashes/tu2-files.sha256", "internal\n")
        self.write("docs/research/github-and-reddit-launch.md", "internal\n")
        self.write("docs/adr/0001.md", "public\n")
        git(self.repo, "add", "-A")
        git(self.repo, "commit", "-q", "-m", "private history")

    def tearDown(self):
        self.tmp.cleanup()

    def write(self, path, text):
        file = self.repo / path
        file.parent.mkdir(parents=True, exist_ok=True)
        file.write_text(text)

    def files(self, ref):
        return set(git(self.repo, "ls-tree", "-r", "--name-only", ref).splitlines())

    def test_public_tree_leaves_out_internal_files(self):
        published(self.repo, "First public commit")
        self.assertEqual(self.files("public"), {"README.md", "docs/adr/0001.md"})

    def test_public_commit_has_no_private_history_and_the_noreply_identity(self):
        published(self.repo, "First public commit")
        self.assertEqual(git(self.repo, "rev-list", "--count", "public"), "1")
        self.assertEqual(git(self.repo, "log", "-1", "--format=%ae %ce", "public"), f"{NOREPLY} {NOREPLY}")
        self.assertEqual(git(self.repo, "log", "-1", "--format=%s", "public"), "First public commit")
        self.assertTrue(git(self.repo, "log", "-1", "--format=%ad %cd", "--date=iso", "public").endswith("+0000"))

    def test_next_publish_stacks_on_the_public_branch(self):
        published(self.repo, "First public commit")
        self.write("README.md", "changed\n")
        git(self.repo, "commit", "-q", "-am", "private change")
        published(self.repo, "Second public commit")
        self.assertEqual(git(self.repo, "rev-list", "--count", "public"), "2")
        self.assertEqual(git(self.repo, "show", "public:README.md"), "changed")

    def test_publish_without_changes_is_refused(self):
        published(self.repo, "First public commit")
        result = publish(self.repo, "Nothing new")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(git(self.repo, "rev-list", "--count", "public"), "1")

    def test_main_and_the_index_stay_untouched(self):
        head = git(self.repo, "rev-parse", "HEAD")
        published(self.repo, "First public commit")
        self.assertEqual(git(self.repo, "rev-parse", "HEAD"), head)
        self.assertEqual(git(self.repo, "status", "--porcelain"), "")


if __name__ == "__main__":
    unittest.main()
