using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEditor.PackageManager.UI;
using UnityEngine;

using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace Lockyaw.VoiceChat.Editor {

    internal static class SampleDependencyInstaller {

        [Serializable]
        private sealed class SamplePackageDependency {

            public string Name;
            public string InstallSource;

            public string GetInstallSource() => string.IsNullOrEmpty(InstallSource) ? Name : InstallSource;

        }

        [Serializable]
        private sealed class SampleEntry {

            public string DisplayName;
            public SamplePackageDependency[] PackageDependencies;

        }

        [Serializable]
        private sealed class SampleConfiguration {

            public SamplePackageDependency[] SharedPackageDependencies;
            public SampleEntry[] SampleEntries;

        }

        private const string PACKAGE_NAME = "lockyaw.voicechat";
        private const string CONFIGURATION_PATH = "Samples~/sampleDependencies.json";

        private static readonly List<SamplePackageDependency> pendingDependencies = new();

        private static ListRequest packageListRequest;
        private static AddAndRemoveRequest packageInstallRequest;

        internal static void HandleAssetsImported(string[] importedAssetPaths) {
            if (packageListRequest != null || packageInstallRequest != null) { return; }

            PackageInfo packageInfo = PackageInfo.FindForPackageName(PACKAGE_NAME);
            if (packageInfo == null) { return; }

            SampleConfiguration configuration = LoadConfiguration(packageInfo);
            if (configuration == null) { return; }

            SampleEntry sampleEntry = FindImportedSampleEntry(packageInfo, configuration, importedAssetPaths);
            if (sampleEntry == null) { return; }

            BuildPendingDependencyList(configuration.SharedPackageDependencies, sampleEntry.PackageDependencies);
            if (pendingDependencies.Count == 0) { return; }

            packageListRequest = Client.List(true);
            EditorApplication.update += WaitForPackageList;
        }

        private static void WaitForPackageList() {
            if (!packageListRequest.IsCompleted) { return; }

            EditorApplication.update -= WaitForPackageList;

            if (packageListRequest.Status != StatusCode.Success) {
                string errorMessage = packageListRequest.Error == null ? "Unknown Package Manager error." : packageListRequest.Error.message;
                Debug.LogError($"Could not inspect sample dependencies: {errorMessage}");
                ClearRequests();
                return;
            }

            RemoveInstalledPackages(packageListRequest.Result);
            packageListRequest = null;

            if (pendingDependencies.Count == 0) { return; }
            if (!ConfirmPackageInstallation()) {
                pendingDependencies.Clear();
                return;
            }

            string[] packagesToAdd = new string[pendingDependencies.Count];
            for (int i = 0; i < pendingDependencies.Count; i++) {
                packagesToAdd[i] = pendingDependencies[i].GetInstallSource();
            }

            pendingDependencies.Clear();
            packageInstallRequest = Client.AddAndRemove(packagesToAdd: packagesToAdd, packagesToRemove: Array.Empty<string>());
            EditorApplication.update += WaitForPackageInstallation;
        }

        private static void WaitForPackageInstallation() {
            if (!packageInstallRequest.IsCompleted) { return; }

            EditorApplication.update -= WaitForPackageInstallation;

            if (packageInstallRequest.Status != StatusCode.Success) {
                string errorMessage = packageInstallRequest.Error == null ? "Unknown Package Manager error." : packageInstallRequest.Error.message;
                Debug.LogError($"Could not install sample dependencies: {errorMessage}");
            }

            packageInstallRequest = null;
        }

        private static void BuildPendingDependencyList(
            SamplePackageDependency[] sharedDependencies,
            SamplePackageDependency[] sampleDependencies
        ) {
            pendingDependencies.Clear();
            AddDependencies(sharedDependencies);
            AddDependencies(sampleDependencies);
        }

        private static void AddDependencies(SamplePackageDependency[] dependencies) {
            if (dependencies == null) { return; }

            for (int i = 0; i < dependencies.Length; i++) {
                SamplePackageDependency dependency = dependencies[i];
                if (dependency == null || string.IsNullOrEmpty(dependency.Name) || ContainsPendingDependency(dependency.Name)) { continue; }
                pendingDependencies.Add(dependency);
            }
        }

        private static void RemoveInstalledPackages(PackageCollection installedPackages) {
            for (int i = pendingDependencies.Count - 1; i >= 0; i--) {
                if (ContainsPackage(installedPackages, pendingDependencies[i].Name)) {
                    pendingDependencies.RemoveAt(i);
                }
            }
        }

        private static void ClearRequests() {
            pendingDependencies.Clear();
            packageListRequest = null;
            packageInstallRequest = null;
        }

        private static SampleConfiguration LoadConfiguration(PackageInfo packageInfo) {
            string configurationPath = Path.Combine(packageInfo.assetPath, CONFIGURATION_PATH);
            if (!File.Exists(configurationPath)) { return null; }

            string configurationText = File.ReadAllText(configurationPath);
            return JsonUtility.FromJson<SampleConfiguration>(configurationText);
        }

        private static SampleEntry FindImportedSampleEntry(
            PackageInfo packageInfo,
            SampleConfiguration configuration,
            string[] importedAssetPaths
        ) {
            if (configuration.SampleEntries == null) { return null; }

            IEnumerable<Sample> samples = Sample.FindByPackage(packageInfo.name, packageInfo.version);
            foreach (Sample sample in samples) {
                if (!sample.isImported || !ContainsImportedAsset(sample.importPath, importedAssetPaths)) { continue; }

                for (int i = 0; i < configuration.SampleEntries.Length; i++) {
                    SampleEntry sampleEntry = configuration.SampleEntries[i];
                    if (sampleEntry != null && sample.displayName == sampleEntry.DisplayName) { return sampleEntry; }
                }
            }

            return null;
        }

        private static bool ContainsImportedAsset(string sampleImportPath, string[] importedAssetPaths) {
            string normalizedSamplePath = sampleImportPath.Replace('\\', '/');
            if (Path.IsPathRooted(normalizedSamplePath)) {
                normalizedSamplePath = FileUtil.GetProjectRelativePath(normalizedSamplePath).Replace('\\', '/');
            }

            for (int i = 0; i < importedAssetPaths.Length; i++) {
                string importedAssetPath = importedAssetPaths[i].Replace('\\', '/');
                if (importedAssetPath == normalizedSamplePath || importedAssetPath.StartsWith($"{normalizedSamplePath}/", StringComparison.Ordinal)) {
                    return true;
                }
            }

            return false;
        }

        private static bool ContainsPackage(PackageCollection installedPackages, string packageName) {
            foreach (PackageInfo installedPackage in installedPackages) {
                if (installedPackage.name == packageName) { return true; }

                DependencyInfo[] dependencies = installedPackage.dependencies;
                for (int i = 0; i < dependencies.Length; i++) {
                    if (dependencies[i].name == packageName) { return true; }
                }
            }

            return false;
        }

        private static bool ContainsPendingDependency(string packageName) {
            for (int i = 0; i < pendingDependencies.Count; i++) {
                if (pendingDependencies[i].Name == packageName) { return true; }
            }

            return false;
        }

        private static bool ConfirmPackageInstallation() {
            string[] packageNames = new string[pendingDependencies.Count];
            for (int i = 0; i < pendingDependencies.Count; i++) {
                packageNames[i] = pendingDependencies[i].Name;
            }

            string dependencyList = string.Join("\n", packageNames);
            return EditorUtility.DisplayDialog(
                "Install Sample Dependencies",
                $"This sample uses packages that are not installed:\n\n{dependencyList}",
                "Install and Import",
                "Import Without Dependencies"
            );
        }

    }

}
