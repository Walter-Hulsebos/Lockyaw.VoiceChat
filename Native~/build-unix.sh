#!/usr/bin/env bash
set -euo pipefail
native_directory="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
package_directory="$(CDPATH= cd -- "$native_directory/.." && pwd)"
case "$(uname -s)" in
    Darwin)
        platform_name=macOS
        platform_guid=ebf7c6f0548e4e4ca2c1d645c5b9a931
        binary_guid=d3a45726a39842389a73d7bcf7ba37c9
        library_name=libLockyawVoiceCapture.dylib
        editor_operating_system=OSX
        standalone_platform=OSXUniversal
        processor=AnyCPU
        platform_arguments=(-DCMAKE_OSX_ARCHITECTURES=x86_64\;arm64)
        ;;
    Linux)
        if [ "$(uname -m)" != x86_64 ]; then
            printf '%s\n' 'This package currently supports Linux x86_64 capture builds.' >&2
            exit 1
        fi
        platform_name=Linux/x86_64
        platform_guid=e3a74d6e8f67445ba7196742bf0bce59
        binary_guid=e69d570fa7a54523a1127cf10c0dab91
        library_name=libLockyawVoiceCapture.so
        editor_operating_system=Linux
        standalone_platform=Linux64
        processor=x86_64
        platform_arguments=()
        ;;
    *)
        printf '%s\n' 'Use build-windows.ps1 on Windows, or this script on Linux/macOS.' >&2
        exit 1
        ;;
esac
build_directory="${1:-$HOME/Documents/Codex/VoiceCapture-Build/$platform_name}"
output_directory="${2:-$package_directory/Runtime/Plugins/$platform_name}"
cmake -S "$native_directory" -B "$build_directory" -DCMAKE_BUILD_TYPE=Release "${platform_arguments[@]}"
cmake --build "$build_directory" --config Release
mkdir -p "$output_directory"
cp "$build_directory/$library_name" "$output_directory/$library_name"
if [ ! -f "$output_directory.meta" ]; then
    cat > "$output_directory.meta" <<METADATA
fileFormatVersion: 2
guid: $platform_guid
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
METADATA
fi
if [ "$editor_operating_system" = Linux ] && [ ! -f "$package_directory/Runtime/Plugins/Linux.meta" ]; then
    cat > "$package_directory/Runtime/Plugins/Linux.meta" <<METADATA
fileFormatVersion: 2
guid: 41d12d547ae94cd5b9c6c2fa063eb05a
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
METADATA
fi
if [ ! -f "$output_directory/$library_name.meta" ]; then
    cat > "$output_directory/$library_name.meta" <<METADATA
fileFormatVersion: 2
guid: $binary_guid
PluginImporter:
  externalObjects: {}
  serializedVersion: 3
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 1
      settings:
        CPU: $processor
        DefaultValueInitialized: true
        OS: $editor_operating_system
  - first:
      Standalone: $standalone_platform
    second:
      enabled: 1
      settings:
        CPU: $processor
  userData:
  assetBundleName:
  assetBundleVariant:
METADATA
fi
printf 'Built %s\n' "$output_directory/$library_name"
