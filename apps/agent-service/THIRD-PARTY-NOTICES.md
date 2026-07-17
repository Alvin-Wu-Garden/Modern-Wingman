# Third-Party Notices

Modern Wingman redistributes the following command-line runtimes in Windows release builds. They are version-locked by `tools/vcs/runtime-manifest.json` and are updated only as part of a Modern Wingman release.

## Git for Windows MinGit 2.55.0.2

- Project: https://gitforwindows.org/
- Source: https://github.com/git-for-windows/git
- License: GNU General Public License version 2
- Bundled license and component notices are preserved under `tools/vcs/git/`.

Git for Windows includes third-party components under their respective licenses. Refer to the files shipped in the MinGit archive for the complete notices and corresponding source links.

## Apache Subversion 1.14.5-4 command-line tools

- Project: https://subversion.apache.org/
- Windows binary distributor: https://www.visualsvn.com/downloads/
- License: Apache License 2.0
- Bundled dependency licenses are preserved under `tools/vcs/svn/Licenses/`.

The Subversion package also includes APR, APR-util, OpenSSL, zlib, and Microsoft runtime components. Their license texts are distributed alongside the binaries.

## Python 3.12.10

- Project: https://www.python.org/
- Distribution: official `python` NuGet runtime package
- License: Python Software Foundation License Version 2
- Bundled license: `tools/runtimes/python/3.12.10/LICENSE.txt`

## Node.js 24.18.0 LTS

- Project: https://nodejs.org/
- License: MIT, with bundled dependencies under their respective licenses
- Bundled license and dependency notices: `tools/runtimes/node/24.18.0/LICENSE`

## DiffPlex 1.9.0

- Project: https://github.com/mmanela/diffplex
- License: Apache License 2.0
- Usage: generates unified and hunk-level ChangeSet diffs in Agent Service.
