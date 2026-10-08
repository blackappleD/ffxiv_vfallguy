using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace vfallguy;

// 插件界面多语言：每种语言是嵌入的 Localization/{code}.json（key -> 文本），en.json 是所有 key 的基准，缺失的 key 回退到英文。
// 默认跟随 Dalamud 界面语言，也可以在窗口中固定一种语言。
// 文本中的 %CURRENCY% / %REGISTRAR% 会替换为游戏数据中的金碟声誉、节目登记员名称（跟随客户端语言）。
public static class Loc
{
    public const string DefaultLanguage = "en";

    // 语言代码（与 Dalamud UiLanguage 一致）-> 本地名称
    public static readonly IReadOnlyList<(string Code, string Name)> SupportedLanguages =
    [
        ("en", "English"),
        ("ja", "日本語"),
        ("de", "Deutsch"),
        ("fr", "Français"),
        ("zh", "简体中文"),
    ];

    private static Dictionary<string, string> _fallback = [];
    private static Dictionary<string, string> _current = [];
    private static Configuration _config = null!;
    private static string? _currencyName;
    private static string? _registrarName;

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    public static void Init(Configuration config)
    {
        _config = config;
        _fallback = Load(DefaultLanguage) ?? [];
        Service.PluginInterface.LanguageChanged += OnDalamudLanguageChanged;
        Apply();
    }

    public static void Dispose()
    {
        Service.PluginInterface.LanguageChanged -= OnDalamudLanguageChanged;
    }

    // 根据配置 / Dalamud 语言重新加载语言表
    public static void Apply()
    {
        var code = ResolveLanguage();
        _current = code == DefaultLanguage ? _fallback : Load(code) ?? _fallback;
        CurrentLanguage = code;
    }

    public static string Get(string key)
    {
        if (_current.TryGetValue(key, out var text) || _fallback.TryGetValue(key, out text))
            return ReplaceTokens(text);

        Service.Log.Verbose($"Missing localization key: {key}");
        return key;
    }

    public static string Format(string key, params object?[] args)
    {
        try
        {
            return string.Format(Get(key), args);
        }
        catch (FormatException)
        {
            // 翻译有误时不能影响绘制，回退到英文模板
            Service.Log.Warning($"Malformed localization string for {key} in {CurrentLanguage}");
            return _fallback.TryGetValue(key, out var text) ? string.Format(ReplaceTokens(text), args) : key;
        }
    }

    public static string GetLanguageName(string code)
        => SupportedLanguages.FirstOrDefault(l => l.Code == code).Name ?? code;

    // Dalamud 当前界面语言对应的插件语言（不支持时为英文）
    public static string DalamudLanguage
        => SupportedLanguages.Any(l => l.Code == Service.PluginInterface.UiLanguage) ? Service.PluginInterface.UiLanguage : DefaultLanguage;

    private static void OnDalamudLanguageChanged(string _)
    {
        // 用户没有固定语言时才跟随 Dalamud
        if (string.IsNullOrEmpty(_config.Language))
            Apply();
    }

    private static string ResolveLanguage()
    {
        var configured = _config.Language;
        return !string.IsNullOrEmpty(configured) && SupportedLanguages.Any(l => l.Code == configured) ? configured : DalamudLanguage;
    }

    private static string ReplaceTokens(string text)
    {
        if (!text.Contains('%'))
            return text;
        _currencyName ??= Service.DataManager.GetExcelSheet<Item>().GetRowOrDefault(ReputationShop.CurrencyItemId)?.Name.ExtractText() ?? "MGF";
        _registrarName ??= Service.DataManager.GetExcelSheet<ENpcResident>().GetRowOrDefault(ReputationShop.RegistratorNpcId)?.Singular.ExtractText() ?? "registrar";
        return text.Replace("%CURRENCY%", _currencyName).Replace("%REGISTRAR%", _registrarName);
    }

    private static Dictionary<string, string>? Load(string code)
    {
        var resource = $"vfallguy.Localization.{code}.json";
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
            if (stream == null)
            {
                Service.Log.Error($"Localization resource {resource} not found.");
                return null;
            }
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        }
        catch (Exception e)
        {
            Service.Log.Error(e, $"Failed to load localization {resource}.");
            return null;
        }
    }
}
