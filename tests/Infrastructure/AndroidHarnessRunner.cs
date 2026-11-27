using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NotchPixel.E2E.Tests.Infrastructure;

public sealed record ApkInspectionResult(
    string ApkPath,
    bool Exists,
    bool HasAndroidManifest,
    bool HasClassesDex,
    bool HasResourcesArsc,
    bool HasSignatureEntries,
    bool SignatureVerified,
    string PackageName,
    string VersionCode,
    string VersionName,
    string LaunchableActivity,
    IReadOnlyList<string> Permissions,
    string BadgingOutput,
    string VerifyOutput);

public sealed record AndroidHarnessResult(
    bool Compiled,
    int ExitCode,
    bool TimedOut,
    int PassedCount,
    int FailedCount,
    IReadOnlyList<string> PassedTests,
    IReadOnlyList<string> FailedTests,
    string StdOut,
    string StdErr);

public static class AndroidHarnessRunner
{
    public static string JbrBinDir => @"C:\Program Files\Android\Android Studio\jbr\bin";
    public static string JavacPath => Path.Combine(JbrBinDir, "javac.exe");
    public static string JavaPath => Path.Combine(JbrBinDir, "java.exe");

    public static string AndroidSdkRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");

    public static string BuildToolsDir
    {
        get
        {
            string preferred = Path.Combine(AndroidSdkRoot, "build-tools", "36.0.0");
            if (Directory.Exists(preferred)) return preferred;
            string btRoot = Path.Combine(AndroidSdkRoot, "build-tools");
            if (Directory.Exists(btRoot))
            {
                string? latest = Directory.EnumerateDirectories(btRoot)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (latest != null) return latest;
            }
            return preferred;
        }
    }

    public static string Aapt2Path => Path.Combine(BuildToolsDir, "aapt2.exe");
    public static string ApkSignerPath => Path.Combine(BuildToolsDir, "apksigner.bat");

    public static string AndroidJarPath
    {
        get
        {
            string preferred = Path.Combine(AndroidSdkRoot, "platforms", "android-37.0", "android.jar");
            if (File.Exists(preferred)) return preferred;
            string platformsRoot = Path.Combine(AndroidSdkRoot, "platforms");
            if (Directory.Exists(platformsRoot))
            {
                string? jar = Directory.EnumerateDirectories(platformsRoot)
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "android.jar"))
                    .FirstOrDefault(File.Exists);
                if (jar != null) return jar;
            }
            return preferred;
        }
    }

    public static string BuildApkScriptPath => Path.Combine(TestWorkspace.AppDirectory, "build_apk.ps1");

    public static IReadOnlyList<string> FindIosArtifactsInApp() => SourceInspector.FindIosArtifactsInApp();

    public static string? FindBuiltApkPath()
    {
        string[] candidates =
        {
            Path.Combine(TestWorkspace.AppDirectory, "main.apk"),
            Path.Combine(TestWorkspace.AppDirectory, "build", "main.apk"),
            Path.Combine(TestWorkspace.AppDirectory, "build", "OpenPixelDeck-debug.apk")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static Task<CliExecutionResult> RunBuildApkScriptAsync(int timeoutMs = 90000)
    {
        return ProcessAndWindowDriver.RunProcessAsync(
            "powershell",
            $"-NoProfile -ExecutionPolicy Bypass -File \"{BuildApkScriptPath}\"",
            timeoutMs,
            TestWorkspace.AppDirectory);
    }

    public static async Task<ApkInspectionResult> InspectApkAsync(string apkPath, int timeoutMs = 15000)
    {
        if (!File.Exists(apkPath))
        {
            return new ApkInspectionResult(
                apkPath, false, false, false, false, false, false,
                string.Empty, string.Empty, string.Empty, string.Empty,
                Array.Empty<string>(), string.Empty, "APK does not exist");
        }

        bool hasManifest = false;
        bool hasDex = false;
        bool hasArsc = false;
        bool hasMetaInfSig = false;

        using (ZipArchive zip = ZipFile.OpenRead(apkPath))
        {
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                string name = entry.FullName;
                if (name.Equals("AndroidManifest.xml", StringComparison.Ordinal)) hasManifest = true;
                else if (name.Equals("classes.dex", StringComparison.Ordinal)) hasDex = true;
                else if (name.Equals("resources.arsc", StringComparison.Ordinal)) hasArsc = true;
                else if (name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) &&
                         (name.EndsWith(".RSA", StringComparison.OrdinalIgnoreCase) ||
                          name.EndsWith(".DSA", StringComparison.OrdinalIgnoreCase) ||
                          name.EndsWith(".EC", StringComparison.OrdinalIgnoreCase) ||
                          name.EndsWith(".SF", StringComparison.OrdinalIgnoreCase)))
                {
                    hasMetaInfSig = true;
                }
            }
        }

        CliExecutionResult badging = await ProcessAndWindowDriver.RunProcessAsync(
            Aapt2Path, $"dump badging \"{apkPath}\"", timeoutMs, TestWorkspace.AppDirectory).ConfigureAwait(false);

        CliExecutionResult verify = await ProcessAndWindowDriver.RunProcessAsync(
            "cmd.exe", $"/c \"\"{ApkSignerPath}\" verify --print-certs \"{apkPath}\"\"", timeoutMs, TestWorkspace.AppDirectory).ConfigureAwait(false);

        string pkg = MatchGroup(badging.StdOut, @"package:\s+name='([^']+)'");
        string verCode = MatchGroup(badging.StdOut, @"versionCode='([^']+)'");
        string verName = MatchGroup(badging.StdOut, @"versionName='([^']+)'");
        string activity = MatchGroup(badging.StdOut, @"launchable-activity:\s+name='([^']+)'");
        var perms = Regex.Matches(badging.StdOut, @"uses-permission:\s+name='([^']+)'")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return new ApkInspectionResult(
            apkPath,
            true,
            hasManifest,
            hasDex,
            hasArsc,
            hasMetaInfSig,
            verify.ExitCode == 0 && !verify.TimedOut,
            pkg,
            verCode,
            verName,
            activity,
            perms,
            badging.StdOut,
            verify.StdOut + verify.StdErr);
    }

    public static async Task<AndroidHarnessResult> RunJvmProtocolTestsAsync(
        int timeoutMs = 15000,
        string? filterTestMethod = null)
    {
        string tempRoot = TestWorkspace.CreateIsolatedTempDir("android_jvm");
        string srcDir = Path.Combine(tempRoot, "src");
        string classesDir = Path.Combine(tempRoot, "classes");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(classesDir);

        try
        {
            EmitAndroidShimClasses(srcDir);

            string harnessDir = Path.Combine(TestWorkspace.RepoRoot, "tests", "android-harness");
            CopyIntoPackageTree(Path.Combine(harnessDir, "AndroidTestShim.java"), srcDir, "com/openpixel/deck/harness");
            CopyIntoPackageTree(Path.Combine(harnessDir, "NotchPixelDeckProtocolTests.java"), srcDir, "com/openpixel/deck/harness");

            string appJavaBase = Path.Combine(TestWorkspace.AppDirectory, "app", "src", "main", "java", "com", "openpixel", "deck");
            CopyIntoPackageTree(Path.Combine(appJavaBase, "network", "WakeOnLanSender.java"), srcDir, "com/openpixel/deck/network");
            CopyIntoPackageTree(Path.Combine(appJavaBase, "qr", "PairingUriParser.java"), srcDir, "com/openpixel/deck/qr");
            CopyIntoPackageTree(Path.Combine(appJavaBase, "security", "SecurePairingStore.java"), srcDir, "com/openpixel/deck/security");
            CopyIntoPackageTree(Path.Combine(appJavaBase, "security", "PinnedDeckClient.java"), srcDir, "com/openpixel/deck/security");

            string[] javaSources = Directory.EnumerateFiles(srcDir, "*.java", SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string sourcesArgFile = Path.Combine(tempRoot, "sources.txt");
            File.WriteAllLines(sourcesArgFile, javaSources.Select(p => "\"" + p.Replace("\\", "\\\\") + "\""));

            // Compile with android.jar as fallback classpath, while our generated shims compile into classesDir
            string cpCompile = File.Exists(AndroidJarPath) ? $"\"{AndroidJarPath}\"" : "\"\"";
            CliExecutionResult compileRes = await ProcessAndWindowDriver.RunProcessAsync(
                JavacPath,
                $"--release 11 -encoding UTF-8 -classpath {cpCompile} -d \"{classesDir}\" \"@{sourcesArgFile}\"",
                timeoutMs,
                tempRoot).ConfigureAwait(false);

            if (compileRes.ExitCode != 0 || compileRes.TimedOut)
            {
                return new AndroidHarnessResult(
                    Compiled: false,
                    ExitCode: compileRes.ExitCode,
                    TimedOut: compileRes.TimedOut,
                    PassedCount: 0,
                    FailedCount: 0,
                    PassedTests: Array.Empty<string>(),
                    FailedTests: Array.Empty<string>(),
                    StdOut: compileRes.StdOut,
                    StdErr: compileRes.StdErr);
            }

            string filterArg = string.IsNullOrWhiteSpace(filterTestMethod) ? string.Empty : $" \"{filterTestMethod}\"";
            CliExecutionResult runRes = await ProcessAndWindowDriver.RunProcessAsync(
                JavaPath,
                $"-cp \"{classesDir}\" com.openpixel.deck.harness.NotchPixelDeckProtocolTests{filterArg}",
                timeoutMs,
                tempRoot).ConfigureAwait(false);

            var passed = new List<string>();
            var failed = new List<string>();
            foreach (string line in runRes.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("[PASS] ", StringComparison.Ordinal))
                    passed.Add(line["[PASS] ".Length..].Trim());
                else if (line.StartsWith("[FAIL] ", StringComparison.Ordinal))
                    failed.Add(line["[FAIL] ".Length..].Trim());
            }

            return new AndroidHarnessResult(
                Compiled: true,
                ExitCode: runRes.ExitCode,
                TimedOut: runRes.TimedOut,
                PassedCount: passed.Count,
                FailedCount: failed.Count,
                PassedTests: passed,
                FailedTests: failed,
                StdOut: runRes.StdOut,
                StdErr: runRes.StdErr);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch { }
        }
    }

    private static void CopyIntoPackageTree(string srcFile, string srcRoot, string relPkgDir)
    {
        if (!File.Exists(srcFile))
            throw new FileNotFoundException($"Required Java source not found: {srcFile}", srcFile);
        string destDir = Path.Combine(srcRoot, relPkgDir.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(destDir);
        File.Copy(srcFile, Path.Combine(destDir, Path.GetFileName(srcFile)), overwrite: true);
    }

    private static string MatchGroup(string text, string pattern)
    {
        Match m = Regex.Match(text, pattern, RegexOptions.CultureInvariant);
        return m.Success ? m.Groups[1].Value : string.Empty;
    }

    private static void EmitAndroidShimClasses(string srcRoot)
    {
        void WriteJava(string relPath, string code)
        {
            string full = Path.Combine(srcRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        WriteJava("android/content/SharedPreferences.java", """
            package android.content;
            public interface SharedPreferences {
                String getString(String key, String defValue);
                Editor edit();
                interface Editor {
                    Editor putString(String key, String value);
                    Editor remove(String key);
                    void apply();
                    boolean commit();
                }
            }
            """);

        WriteJava("android/content/Context.java", """
            package android.content;
            import android.net.wifi.WifiManager;
            import com.openpixel.deck.harness.AndroidTestShim;
            import java.util.Map;
            public class Context {
                public static final int MODE_PRIVATE = 0;
                public static final String WIFI_SERVICE = "wifi";
                public Context getApplicationContext() { return this; }
                public Object getSystemService(String name) {
                    if (WIFI_SERVICE.equals(name)) return new WifiManager();
                    return null;
                }
                public SharedPreferences getSharedPreferences(String name, int mode) {
                    final Map<String, String> bucket = AndroidTestShim.getPrefsBucket(name);
                    return new SharedPreferences() {
                        @Override public String getString(String key, String defValue) {
                            String v = bucket.get(key);
                            return v != null ? v : defValue;
                        }
                        @Override public Editor edit() {
                            return new Editor() {
                                @Override public Editor putString(String key, String value) {
                                    if (value == null) bucket.remove(key);
                                    else bucket.put(key, value);
                                    return this;
                                }
                                @Override public Editor remove(String key) {
                                    bucket.remove(key);
                                    return this;
                                }
                                @Override public void apply() {}
                                @Override public boolean commit() { return true; }
                            };
                        }
                    };
                }
            }
            """);

        WriteJava("android/net/wifi/WifiInfo.java", """
            package android.net.wifi;
            import com.openpixel.deck.harness.AndroidTestShim;
            public class WifiInfo {
                public int getIpAddress() {
                    return AndroidTestShim.getLocalIpv4LittleEndian();
                }
            }
            """);

        WriteJava("android/net/wifi/WifiManager.java", """
            package android.net.wifi;
            public class WifiManager {
                public WifiInfo getConnectionInfo() { return new WifiInfo(); }
            }
            """);

        WriteJava("android/net/Uri.java", """
            package android.net;
            import java.net.URI;
            import java.net.URLDecoder;
            import java.nio.charset.StandardCharsets;
            import java.util.LinkedHashMap;
            import java.util.Map;
            public final class Uri {
                private final URI uri;
                private final Map<String, String> params = new LinkedHashMap<>();
                private Uri(String raw) {
                    this.uri = URI.create(raw);
                    String q = uri.getRawQuery();
                    if (q != null) {
                        for (String part : q.split("&")) {
                            int eq = part.indexOf('=');
                            if (eq > 0) {
                                params.put(
                                    URLDecoder.decode(part.substring(0, eq), StandardCharsets.UTF_8),
                                    URLDecoder.decode(part.substring(eq + 1), StandardCharsets.UTF_8));
                            }
                        }
                    }
                }
                public static Uri parse(String s) { return new Uri(s); }
                public String getScheme() { return uri.getScheme(); }
                public String getHost() { return uri.getHost(); }
                public int getPort() { return uri.getPort(); }
                public String getPath() { return uri.getPath(); }
                public String getQueryParameter(String key) { return params.get(key); }
            }
            """);

        WriteJava("android/os/Build.java", """
            package android.os;
            public class Build {
                public static String MODEL = "NotchPixelE2E-Android";
                public static class VERSION {
                    public static String RELEASE = "15";
                }
            }
            """);

        WriteJava("android/security/keystore/KeyProperties.java", """
            package android.security.keystore;
            public final class KeyProperties {
                public static final String KEY_ALGORITHM_AES = "AES";
                public static final int PURPOSE_ENCRYPT = 1;
                public static final int PURPOSE_DECRYPT = 2;
                public static final String BLOCK_MODE_GCM = "GCM";
                public static final String ENCRYPTION_PADDING_NONE = "NoPadding";
            }
            """);

        WriteJava("android/security/keystore/KeyGenParameterSpec.java", """
            package android.security.keystore;
            import java.security.spec.AlgorithmParameterSpec;
            public final class KeyGenParameterSpec implements AlgorithmParameterSpec {
                private final String alias;
                private final int keySize;
                private KeyGenParameterSpec(String alias, int keySize) {
                    this.alias = alias;
                    this.keySize = keySize;
                }
                public String getKeystoreAlias() { return alias; }
                public int getKeySize() { return keySize; }
                public static final class Builder {
                    private final String alias;
                    private int keySize = 256;
                    public Builder(String keystoreAlias, int purposes) { this.alias = keystoreAlias; }
                    public Builder setBlockModes(String... blockModes) { return this; }
                    public Builder setEncryptionPaddings(String... paddings) { return this; }
                    public Builder setKeySize(int keySize) { this.keySize = keySize; return this; }
                    public KeyGenParameterSpec build() { return new KeyGenParameterSpec(alias, keySize); }
                }
            }
            """);

        WriteJava("android/util/Base64.java", """
            package android.util;
            import com.openpixel.deck.harness.AndroidTestShim;
            public final class Base64 {
                public static final int DEFAULT = 0;
                public static final int NO_WRAP = 2;
                public static String encodeToString(byte[] input, int flags) {
                    return AndroidTestShim.base64Encode(input);
                }
                public static byte[] decode(String str, int flags) {
                    return AndroidTestShim.base64Decode(str);
                }
            }
            """);

        WriteJava("org/json/JSONException.java", """
            package org.json;
            public class JSONException extends Exception {
                public JSONException(String message) { super(message); }
                public JSONException(Throwable cause) { super(cause); }
            }
            """);

        WriteJava("org/json/JSONObject.java", """
            package org.json;
            import com.openpixel.deck.harness.AndroidTestShim;
            import java.util.LinkedHashMap;
            import java.util.List;
            import java.util.Map;
            public class JSONObject {
                final Map<String, Object> map;
                public JSONObject() { this.map = new LinkedHashMap<>(); }
                public JSONObject(String json) throws JSONException {
                    try { this.map = AndroidTestShim.parseJsonObject(json); }
                    catch (Exception ex) { throw new JSONException(ex); }
                }
                JSONObject(Map<String, Object> existing) { this.map = existing; }
                public JSONObject put(String name, Object value) throws JSONException {
                    if (value instanceof JSONObject) map.put(name, ((JSONObject) value).map);
                    else if (value instanceof JSONArray) map.put(name, ((JSONArray) value).list);
                    else map.put(name, value);
                    return this;
                }
                public JSONObject put(String name, int value) throws JSONException { map.put(name, value); return this; }
                public JSONObject put(String name, long value) throws JSONException { map.put(name, value); return this; }
                public JSONObject put(String name, double value) throws JSONException { map.put(name, value); return this; }
                public JSONObject put(String name, boolean value) throws JSONException { map.put(name, value); return this; }
                public boolean has(String name) { return map.containsKey(name); }
                public String optString(String name) { return optString(name, ""); }
                public String optString(String name, String fallback) {
                    Object v = map.get(name);
                    return v == null ? fallback : String.valueOf(v);
                }
                public int optInt(String name) { return optInt(name, 0); }
                public int optInt(String name, int fallback) {
                    Object v = map.get(name);
                    if (v instanceof Number) return ((Number) v).intValue();
                    if (v instanceof String) { try { return Integer.parseInt((String) v); } catch (Exception ignored) {} }
                    return fallback;
                }
                public long optLong(String name) { return optLong(name, 0L); }
                public long optLong(String name, long fallback) {
                    Object v = map.get(name);
                    if (v instanceof Number) return ((Number) v).longValue();
                    if (v instanceof String) { try { return Long.parseLong((String) v); } catch (Exception ignored) {} }
                    return fallback;
                }
                public boolean optBoolean(String name) { return optBoolean(name, false); }
                public boolean optBoolean(String name, boolean fallback) {
                    Object v = map.get(name);
                    if (v instanceof Boolean) return (Boolean) v;
                    if (v instanceof String) return Boolean.parseBoolean((String) v);
                    return fallback;
                }
                @SuppressWarnings("unchecked")
                public JSONObject optJSONObject(String name) {
                    Object v = map.get(name);
                    return v instanceof Map ? new JSONObject((Map<String, Object>) v) : null;
                }
                @SuppressWarnings("unchecked")
                public JSONArray optJSONArray(String name) {
                    Object v = map.get(name);
                    return v instanceof List ? new JSONArray((List<Object>) v) : null;
                }
                @Override public String toString() { return AndroidTestShim.serializeJsonValue(map); }
            }
            """);

        WriteJava("org/json/JSONArray.java", """
            package org.json;
            import com.openpixel.deck.harness.AndroidTestShim;
            import java.util.ArrayList;
            import java.util.List;
            import java.util.Map;
            public class JSONArray {
                final List<Object> list;
                public JSONArray() { this.list = new ArrayList<>(); }
                public JSONArray(String json) throws JSONException {
                    try { this.list = AndroidTestShim.parseJsonArray(json); }
                    catch (Exception ex) { throw new JSONException(ex); }
                }
                JSONArray(List<Object> existing) { this.list = existing; }
                public int length() { return list.size(); }
                public JSONArray put(Object value) {
                    if (value instanceof JSONObject) list.add(((JSONObject) value).map);
                    else if (value instanceof JSONArray) list.add(((JSONArray) value).list);
                    else list.add(value);
                    return this;
                }
                @SuppressWarnings("unchecked")
                public JSONObject getJSONObject(int index) throws JSONException {
                    if (index < 0 || index >= list.size()) throw new JSONException("Index out of bounds: " + index);
                    Object v = list.get(index);
                    if (!(v instanceof Map)) throw new JSONException("Value at " + index + " is not an object");
                    return new JSONObject((Map<String, Object>) v);
                }
                @SuppressWarnings("unchecked")
                public JSONObject optJSONObject(int index) {
                    if (index < 0 || index >= list.size()) return null;
                    Object v = list.get(index);
                    return v instanceof Map ? new JSONObject((Map<String, Object>) v) : null;
                }
                @Override public String toString() { return AndroidTestShim.serializeJsonValue(list); }
            }
            """);
    }
}
