"""Exercise the actual release workflow guard with recorded-style API fixtures."""
import base64
import hashlib
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import struct
import tempfile
import textwrap
import unittest
import zipfile
from unittest.mock import patch


REPO = "kurasis/FindCopy"
SHA = "c" * 40
WORKFLOW = Path(__file__).resolve().parents[1] / ".github/workflows/release.yml"
BLOCKS = [textwrap.dedent(block.split("\n          PY", 1)[0])
          for block in WORKFLOW.read_text().split("python3 - <<'PY'\n")[1:]]
SOURCE, PACKAGE = BLOCKS[:2]


class ReleaseSourceTests(unittest.TestCase):
    def validate(self, tag_object=None, annotated_commit=SHA, release_tag="v1.0.0", **run_overrides):
        run = dict(status="completed", conclusion="success", event="push", head_branch="main",
                   repository=dict(full_name=REPO), head_repository=dict(full_name=REPO),
                   path=".github/workflows/validate.yml", head_sha=SHA, run_attempt=2)
        run.update(run_overrides)
        project = base64.b64encode(b"<Project><PropertyGroup><Version>1.0.0</Version></PropertyGroup></Project>").decode()
        responses = {
            f"repos/{REPO}/actions/runs/123": run,
            f"repos/{REPO}/contents/src/FindCopy.App/FindCopy.App.csproj?ref={SHA}": dict(content=project),
            f"repos/{REPO}/git/matching-refs/tags/v1.0.0": [] if tag_object is None else [dict(ref="refs/tags/v1.0.0", object=tag_object)],
            f"repos/{REPO}/git/tags/{'a' * 40}": dict(object=dict(type="commit", sha=annotated_commit)),
        }

        def api(command, *, text):
            self.assertEqual(command[:2], ["gh", "api"])
            return json.dumps(responses[command[2]])

        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output"
            env = dict(GH_REPO=REPO, RELEASE_TAG=release_tag, VALIDATION_RUN="123", GITHUB_OUTPUT=str(output))
            with patch.dict(os.environ, env), patch.object(subprocess, "check_output", side_effect=api), contextlib.redirect_stdout(io.StringIO()):
                exec(compile(SOURCE, str(WORKFLOW), "exec"), {})
            self.assertEqual(output.read_text(), f"sha={SHA}\nversion=1.0.0\n")

    def test_new_tag(self):
        self.validate()

    def test_matching_lightweight_tag(self):
        self.validate(dict(type="commit", sha=SHA))

    def test_matching_annotated_tag(self):
        self.validate(dict(type="tag", sha="a" * 40))

    def test_mismatched_lightweight_tag(self):
        with self.assertRaises(AssertionError):
            self.validate(dict(type="commit", sha="d" * 40))

    def test_mismatched_annotated_tag(self):
        with self.assertRaises(AssertionError):
            self.validate(dict(type="tag", sha="a" * 40), annotated_commit="d" * 40)

    def test_failed_validation(self):
        with self.assertRaises(AssertionError):
            self.validate(conclusion="failure")

    def test_pull_request_source(self):
        with self.assertRaises(AssertionError):
            self.validate(event="pull_request")

    def test_wrong_validation_workflow(self):
        with self.assertRaises(AssertionError):
            self.validate(path=".github/workflows/release.yml")

    def test_tag_must_match_the_validated_application_version(self):
        with self.assertRaises(AssertionError):
            self.validate(release_tag="v1.0.1")

    def test_unsafe_tag_is_rejected_before_reading_source(self):
        with self.assertRaises(AssertionError):
            self.validate(release_tag="v1.0.0/../../unexpected")


class ReleasePackagingTests(unittest.TestCase):
    def package(self, *, bad_architecture=False, bad_signature=False, tag="v1.0.2"):
        with tempfile.TemporaryDirectory() as directory:
            original = Path.cwd()
            os.chdir(directory)
            try:
                fixtures = {}
                for arch, machine in [("x64", 0x8664), ("arm64", 0xaa64)]:
                    data = bytearray(256)
                    data[:2] = b"MZ"
                    struct.pack_into("<I", data, 0x3c, 0x80)
                    data[0x80:0x84] = b"bad!" if bad_signature else b"PE\0\0"
                    struct.pack_into("<H", data, 0x84, 0 if bad_architecture else machine)
                    source = Path("downloaded") / arch / "FindCopy.exe"
                    source.parent.mkdir(parents=True)
                    source.write_bytes(data)
                    fixtures[arch] = bytes(data)
                with patch.dict(os.environ, RELEASE_TAG=tag), contextlib.redirect_stdout(io.StringIO()):
                    exec(compile(PACKAGE, str(WORKFLOW), "exec"), {})
                output = Path("release-assets")
                version = tag[1:]
                expected = {f"FindCopy-{version}-win-{arch}.{suffix}"
                            for arch in fixtures for suffix in ("exe", "zip")}
                self.assertEqual({p.name for p in output.iterdir()}, expected | {"SHA256SUMS.txt"})
                for arch, data in fixtures.items():
                    self.assertEqual((output / f"FindCopy-{version}-win-{arch}.exe").read_bytes(), data)
                    with zipfile.ZipFile(output / f"FindCopy-{version}-win-{arch}.zip") as archive:
                        self.assertEqual(archive.namelist(), ["FindCopy.exe"])
                        self.assertEqual(archive.read("FindCopy.exe"), data)
                checksums = {}
                for line in (output / "SHA256SUMS.txt").read_text().splitlines():
                    digest, name = line.split("  ")
                    checksums[name] = digest
                    self.assertEqual(digest, hashlib.sha256((output / name).read_bytes()).hexdigest())
                self.assertEqual(set(checksums), expected)
            finally:
                os.chdir(original)

    def test_versioned_assets_preserve_the_validated_bytes_and_hashes(self):
        self.package()

    def test_another_version_controls_every_distributable_name(self):
        self.package(tag="v2.3.4")

    def test_wrong_executable_architecture_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.package(bad_architecture=True)

    def test_invalid_pe_signature_is_rejected(self):
        with self.assertRaises(AssertionError):
            self.package(bad_signature=True)

    def test_unsafe_version_cannot_create_asset_paths(self):
        with self.assertRaises(AssertionError):
            self.package(tag="v1.0.2/../../unexpected")


if __name__ == "__main__":
    unittest.main()
