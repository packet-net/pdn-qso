#!/usr/bin/env bash
# Builds the pdn-qso .deb for one architecture.
#
#   debian/build-deb.sh <version> [amd64|arm64|armhf] [outdir]
#
# Produces <outdir>/pdn-qso_<version>_<arch>.deb containing a self-contained single-file
# build (no .NET runtime dependency on the target). Default outdir is <repo>/artifacts.
#
# Unlike pdn-soundmodem, this is a program somebody runs, not a service: no systemd unit, no
# system user, and no seeded config. Settings are per user and pdn-qso writes them to
# ~/.config/pdn-qso/config.json the first time it is run, so there is nothing for a maintainer
# script to place and nothing for a purge to clean up.
#
# Layout note: PublishSingleFile bundles the managed assemblies and the runtime but leaves
# per-package native shims loose beside the executable, and .NET resolves those relative to
# the real path of the running binary. So the payload goes in /usr/lib/pdn-qso/ and
# /usr/bin/pdn-qso is a symlink into it. Installing only the bare executable to /usr/bin would
# drop the shims.
set -euo pipefail

VERSION="${1:?usage: build-deb.sh <version> [arch] [outdir]}"
ARCH="${2:-amd64}"

case "$ARCH" in
  amd64) RID=linux-x64 ;;
  arm64) RID=linux-arm64 ;;
  armhf) RID=linux-arm ;;
  *) echo "unsupported arch $ARCH" >&2; exit 2 ;;
esac

# dpkg-deb ships in the Essential `dpkg` package, so this only trips on a non-Debian host.
command -v dpkg-deb >/dev/null || { echo "dpkg-deb not found - this needs a Debian-family host" >&2; exit 3; }

HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(dirname "$HERE")"
OUTDIR="${3:-$ROOT/artifacts}"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

PKGDIR=/usr/lib/pdn-qso
DOCDIR=/usr/share/doc/pdn-qso

dotnet publish "$ROOT/src/PdnQso/PdnQso.csproj" \
  --configuration Release \
  --runtime "$RID" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:Version="$VERSION" \
  -p:DebugType=none \
  -p:GenerateDocumentationFile=false \
  --output "$STAGE/publish"

mkdir -p "$STAGE/root$PKGDIR" \
         "$STAGE/root/usr/bin" \
         "$STAGE/root$DOCDIR" \
         "$STAGE/root/DEBIAN"

install -m 0755 "$STAGE/publish/pdn-qso" "$STAGE/root$PKGDIR/pdn-qso"
# Native shims published alongside the bundle.
for so in "$STAGE"/publish/*.so; do
  [ -e "$so" ] || continue
  install -m 0644 "$so" "$STAGE/root$PKGDIR/$(basename "$so")"
done
ln -s "..${PKGDIR#/usr}/pdn-qso" "$STAGE/root/usr/bin/pdn-qso"

install -m 0644 "$HERE/copyright" "$STAGE/root$DOCDIR/copyright"
install -m 0644 "$ROOT/README.md" "$STAGE/root$DOCDIR/README.md"

# Debian changelog. A numeric SOURCE_DATE_EPOCH keeps rebuilds of a tag byte-identical;
# anything else (an ISO string from a CI event payload, say) falls back to now rather than
# failing the build on `date -R`.
case "${SOURCE_DATE_EPOCH:-}" in
  ''|*[!0-9]*) CHANGELOG_DATE="$(date -R)" ;;
  *)           CHANGELOG_DATE="$(date -R --date="@$SOURCE_DATE_EPOCH")" ;;
esac
cat > "$STAGE/changelog.Debian" <<EOF
pdn-qso ($VERSION) unstable; urgency=medium

  * Release $VERSION. See https://github.com/packet-net/pdn-qso/releases/tag/v$VERSION

 -- Tom Fanning M0LTE <tom@m0lte.uk>  $CHANGELOG_DATE
EOF
gzip -9n -c "$STAGE/changelog.Debian" > "$STAGE/root$DOCDIR/changelog.Debian.gz"
chmod 0644 "$STAGE/root$DOCDIR/changelog.Debian.gz"

INSTALLED_SIZE="$(du -k -s --exclude=DEBIAN "$STAGE/root" | cut -f1)"

cat > "$STAGE/root/DEBIAN/control" <<EOF
Package: pdn-qso
Version: $VERSION
Architecture: $ARCH
Maintainer: Tom Fanning M0LTE <tom@m0lte.uk>
Installed-Size: $INSTALLED_SIZE
Depends: libc6, libgcc-s1, libstdc++6, libasound2 | libasound2t64
Section: hamradio
Priority: optional
Homepage: https://github.com/packet-net/pdn-qso
Description: Interactive two-way testing over the pdn-soundmodem modems
 A full-screen terminal tool for two stations to talk to each other over any of
 the pdn-soundmodem modems: a Monitor pane that shows every frame heard, a
 keyboard-to-keyboard Chat with an acknowledged ARQ, a fountain-coded File
 transfer, and a Perf mode that produces numbers you can screenshot.
 .
 Drives a FlexRadio 6000-series over the LAN, a sound card with a CM108 PTT
 widget, or a public UberSDR web receiver for listening only.
 .
 Run it as yourself: there is no service and no system-wide configuration.
 Settings are written to ~/.config/pdn-qso/config.json on first run.
 .
 AGPL-3.0-or-later.
EOF

mkdir -p "$OUTDIR"
DEB="$OUTDIR/pdn-qso_${VERSION}_${ARCH}.deb"
dpkg-deb --build --root-owner-group "$STAGE/root" "$DEB"
echo "built: $DEB"
