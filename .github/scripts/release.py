import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


VERSION = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def git(*args):
    return subprocess.check_output(["git", *args], text=True).strip()


def api(path):
    return json.loads(subprocess.check_output(["gh", "api", path], text=True))


def output(**values):
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        for key, value in values.items():
            require("\n" not in str(value), "Invalid workflow output")
            stream.write(f"{key}={value}\n")


def metadata(commit):
    require(re.fullmatch(r"[0-9a-f]{40}", commit), "Invalid source commit")
    info = json.loads(git("show", f"{commit}:AutoClay/modinfo.json"))
    project = ET.fromstring(git("show", f"{commit}:AutoClay/AutoClay.csproj"))
    require(info["modid"] == "vsautoclay", "Unexpected mod ID")
    require(re.fullmatch(VERSION, info["version"]), "Expected a stable X.Y.Z version")
    require(project.findtext(".//Version") == info["version"], "Project and mod versions differ")
    require(info["authors"] == ["MErevGe"] and info["side"] == "Client", "Unexpected mod identity")
    return info


def inspect_package(path, info, commit):
    assets = git("ls-tree", "-r", "--name-only", commit, "AutoClay/assets").splitlines()
    expected = {"AutoClay.dll", "modinfo.json", *(p.removeprefix("AutoClay/") for p in assets)}
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)) and set(names) == expected, "Unexpected ZIP contents")
        require(archive.testzip() is None, "Corrupt ZIP entry")
        require(json.loads(archive.read("modinfo.json")) == info, "ZIP metadata differs from source")
        require(archive.read("AutoClay.dll").startswith(b"MZ"), "Missing compiled mod assembly")
        for name in expected - {"AutoClay.dll", "modinfo.json"}:
            source = subprocess.check_output(["git", "show", f"{commit}:AutoClay/{name}"])
            require(archive.read(name) == source, f"ZIP asset differs from source: {name}")
    return hashlib.sha256(path.read_bytes()).hexdigest()


def package():
    commit = os.environ["GITHUB_SHA"]
    info = metadata(commit)
    filename = f"vsautoclay_{info['version']}.zip"
    digest = inspect_package(Path("Releases") / filename, info, commit)
    manifest = {"commit": commit, "version": info["version"], "file": filename, "sha256": digest}
    Path("Releases/release.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    Path("Releases/SHA256SUMS").write_text(f"{digest}  {filename}\n", encoding="utf-8")
    output(package=filename)


def verify_tag(tag, main_commit):
    require(re.fullmatch("v" + VERSION, tag), "Expected an annotated, signed vX.Y.Z tag")
    ref = f"refs/tags/{tag}"
    require(git("cat-file", "-t", ref) == "tag", "Lightweight tags cannot be released")
    header = git("cat-file", "tag", ref).split("\n\n", 1)[0].splitlines()
    require(f"tag {tag}" in header and "type commit" in header, "Invalid tag target or embedded name")
    signers = str(Path(".github/release-signers").resolve())
    git("-c", "gpg.format=ssh", "-c", f"gpg.ssh.allowedSignersFile={signers}", "verify-tag", ref)
    commit = git("rev-parse", f"{ref}^{{commit}}")
    subprocess.run(["git", "merge-base", "--is-ancestor", commit, main_commit], check=True)
    info = metadata(commit)
    require(tag == "v" + info["version"], "Tag and mod versions differ")
    return commit


def prepare():
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    repo = os.environ["GITHUB_REPOSITORY"]
    prefix = f"repos/{repo}"
    main_commit = os.environ["GITHUB_SHA"]
    require(git("rev-parse", "HEAD") == main_commit, "Release policy must come from the workflow commit")
    automatic = os.environ["GITHUB_EVENT_NAME"] == "workflow_run"
    tag = event["workflow_run"]["head_branch"] if automatic else os.environ.get("RELEASE_TAG", "")
    dry_run = not automatic and os.environ.get("DRY_RUN") == "true"
    require(automatic or os.environ["GITHUB_REF"] == "refs/heads/main", "Run releases from main")
    require(tag or dry_run, "Publishing requires a signed version tag")
    commit = verify_tag(tag, main_commit) if tag else main_commit
    info = metadata(commit)
    if automatic:
        run = api(f"{prefix}/actions/runs/{int(event['workflow_run']['id'])}")
    else:
        runs = api(f"{prefix}/actions/workflows/verify.yml/runs?head_sha={commit}&status=success&per_page=100")
        candidates = [r for r in runs["workflow_runs"] if r["head_branch"] == (tag or "main") and r["event"] in ("push", "workflow_dispatch")]
        require(candidates, "No successful Verify run for this tag/commit; run Verify first")
        run = max(candidates, key=lambda r: r["id"])
    workflow = api(f"{prefix}/actions/workflows/verify.yml")
    require(run["workflow_id"] == workflow["id"] and run["path"] == ".github/workflows/verify.yml", "Unexpected build workflow")
    require(run["repository"]["full_name"] == repo and run["head_repository"]["full_name"] == repo, "Unexpected build repository")
    require(run["event"] in ("push", "workflow_dispatch") and run["head_branch"] == (tag or "main"), "Unexpected build event")
    require(run["head_sha"] == commit and run["conclusion"] == "success" and run["status"] == "completed", "Build is not successful for this commit")
    artifacts = api(f"{prefix}/actions/runs/{run['id']}/artifacts?per_page=100")["artifacts"]
    artifacts = [a for a in artifacts if a["name"] == f"mod-package-{run['run_attempt']}" and not a["expired"]]
    require(len(artifacts) == 1, "Expected one unexpired artifact from the latest successful run attempt")
    output(tag=tag, version=info["version"], commit=commit, run_id=run["id"], artifact_id=artifacts[0]["id"], publish=str(not dry_run).lower())


def check():
    commit = os.environ["SOURCE_COMMIT"]
    info = metadata(commit)
    filename = f"vsautoclay_{info['version']}.zip"
    directory = Path("release-artifact")
    require({p.name for p in directory.iterdir()} == {filename, "SHA256SUMS", "release.json"}, "Unexpected artifact files")
    digest = inspect_package(directory / filename, info, commit)
    expected = {"commit": commit, "version": info["version"], "file": filename, "sha256": digest}
    require(json.loads((directory / "release.json").read_text()) == expected, "Artifact provenance mismatch")
    require((directory / "SHA256SUMS").read_text() == f"{digest}  {filename}\n", "Artifact checksum mismatch")
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as stream:
        stream.write(f"Validated `{filename}` from `{commit}`.\n\nSHA-256: `{digest}`\n")


if __name__ == "__main__":
    commands = {"package": package, "prepare": prepare, "check": check}
    require(len(sys.argv) == 2 and sys.argv[1] in commands, "Use package, prepare or check")
    commands[sys.argv[1]]()
