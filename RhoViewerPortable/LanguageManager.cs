using System.Xml;

namespace RhoViewerPortable;

internal sealed record LanguagePackageInfo(string FileName, string DisplayName, string Locale, string ShortName)
{
    public override string ToString() => ShortName;
}

/// <summary>
/// Loads the user-editable language packages shipped beside the portable app.
/// Packages are data-only XML; DTDs and external resolvers are disabled.
/// </summary>
internal static class LanguageManager
{
    private const string DefaultPackage = "zh-Hant.xml";
    private static readonly string[] SupportedFiles = ["zh-Hans.xml", "zh-Hant.xml", "ko-KR.xml", "en-US.xml"];
    private static readonly Dictionary<string, string> Values = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> TraditionalFallback = new(StringComparer.OrdinalIgnoreCase);
    private static bool _initialized;
    private static string _currentFileName = DefaultPackage;

    public static string LanguageDirectory => Path.Combine(AppContext.BaseDirectory, "languages");
    public static string CurrentFileName { get { EnsureInitialized(); return _currentFileName; } }

    public static void Initialize(string? packageFileName)
    {
        Values.Clear();
        TraditionalFallback.Clear();
        var requested = NormalizePackageFileName(packageFileName);
        if (TryLoadPackage(requested, Values, out _))
        {
            _currentFileName = requested;
        }
        else if (TryLoadPackage(DefaultPackage, Values, out _))
        {
            _currentFileName = DefaultPackage;
        }
        else
        {
            _currentFileName = DefaultPackage;
            Values.Clear();
        }

        if (!string.Equals(_currentFileName, DefaultPackage, StringComparison.OrdinalIgnoreCase))
            TryLoadPackage(DefaultPackage, TraditionalFallback, out _);
        _initialized = true;
    }

    public static IReadOnlyList<LanguagePackageInfo> GetPackages()
    {
        EnsureInitialized();
        var packages = new List<LanguagePackageInfo>();
        foreach (var fileName in SupportedFiles)
        {
            if (TryLoadPackage(fileName, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), out var info) && info is not null)
                packages.Add(info);
        }
        return packages;
    }

    public static string NormalizePackageFileName(string? fileName)
    {
        var candidate = Path.GetFileName(fileName?.Trim());
        return SupportedFiles.FirstOrDefault(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)) ?? DefaultPackage;
    }

    public static string Translate(string key, string fallback)
    {
        EnsureInitialized();
        if (Values.TryGetValue(key, out var value) && value.Length > 0)
            return value;
        if (TraditionalFallback.TryGetValue(key, out value) && value.Length > 0)
            return value;
        return fallback;
    }

    /// <summary>Uses an existing Traditional Chinese UI string as a stable lookup key.</summary>
    public static string TranslateText(string fallback)
    {
        if (string.IsNullOrEmpty(fallback))
            return fallback;
        return Translate("text." + fallback, fallback);
    }

    public static string Format(string key, string fallback, params object?[] arguments)
    {
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, Translate(key, fallback), arguments);
    }

    public static void Apply(Form form)
    {
        EnsureInitialized();
        ApplyControl(form);
    }

    private static void ApplyControl(Control control)
    {
        if (control is TextBoxBase textBox)
        {
            if (textBox is TextBox singleLine && !string.IsNullOrEmpty(singleLine.PlaceholderText))
                singleLine.PlaceholderText = TranslateText(singleLine.PlaceholderText);
        }
        else if (CanTranslateControlText(control))
        {
            control.TextChanged += TranslateControlTextChanged;
            control.Text = TranslateText(control.Text);
        }

        if (control is ComboBox comboBox)
        {
            for (var index = 0; index < comboBox.Items.Count; index++)
            {
                if (comboBox.Items[index] is string item)
                    comboBox.Items[index] = TranslateText(item);
            }
        }

        if (control is DataGridView grid)
        {
            foreach (DataGridViewColumn column in grid.Columns)
                column.HeaderText = TranslateText(column.HeaderText);
        }

        if (control is ToolStrip toolStrip)
        {
            foreach (ToolStripItem item in toolStrip.Items)
                ApplyToolStripItem(item);
        }

        foreach (Control child in control.Controls)
            ApplyControl(child);
    }

    private static bool CanTranslateControlText(Control control) =>
        control is Form or Label or Button or CheckBox or RadioButton or GroupBox or TabPage or LinkLabel;

    private static void TranslateControlTextChanged(object? sender, EventArgs e)
    {
        if (sender is Control control)
        {
            var translated = TranslateText(control.Text);
            if (!string.Equals(translated, control.Text, StringComparison.Ordinal))
                control.Text = translated;
        }
    }

    private static void ApplyToolStripItem(ToolStripItem item)
    {
        item.TextChanged += TranslateToolStripTextChanged;
        item.Text = TranslateText(item.Text);
        if (!string.IsNullOrEmpty(item.ToolTipText))
            item.ToolTipText = TranslateText(item.ToolTipText);

        if (item is ToolStripControlHost host)
            ApplyControl(host.Control);
        if (item is ToolStripDropDownItem dropDown)
        {
            foreach (ToolStripItem child in dropDown.DropDownItems)
                ApplyToolStripItem(child);
        }
    }

    private static void TranslateToolStripTextChanged(object? sender, EventArgs e)
    {
        if (sender is ToolStripItem item)
        {
            var translated = TranslateText(item.Text);
            if (!string.Equals(translated, item.Text, StringComparison.Ordinal))
                item.Text = translated;
        }
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
            Initialize(DefaultPackage);
    }

    private static bool TryLoadPackage(string fileName, Dictionary<string, string> destination, out LanguagePackageInfo? info)
    {
        info = null;
        if (!SupportedFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            return false;

        var path = Path.Combine(LanguageDirectory, fileName);
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length is <= 0 or > 1_048_576)
                return false;

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 1_048_576,
                MaxCharactersFromEntities = 0,
                IgnoreComments = true
            };
            var document = new XmlDocument { XmlResolver = null };
            using (var reader = XmlReader.Create(path, settings))
                document.Load(reader);

            var root = document.DocumentElement;
            if (root is null || !string.Equals(root.Name, "language", StringComparison.OrdinalIgnoreCase))
                return false;

            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stringNodes = root.SelectNodes("./string");
            if (stringNodes is not null)
            {
                foreach (XmlNode node in stringNodes)
                {
                    if (node is not XmlElement element)
                        continue;
                    var key = element.GetAttribute("key").Trim();
                    if (key.Length == 0 || key.Length > 256 || element.InnerText.Length > 8192)
                        continue;
                    parsed[key] = element.InnerText;
                    if (parsed.Count > 10_000)
                        return false;
                }
            }

            if (parsed.Count == 0)
                return false;

            destination.Clear();
            foreach (var pair in parsed)
                destination[pair.Key] = pair.Value;

            var locale = root.GetAttribute("locale");
            var displayName = root.GetAttribute("name");
            info = new LanguagePackageInfo(fileName, displayName.Length == 0 ? fileName : displayName, locale, GetShortName(fileName));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException or ArgumentException)
        {
            return false;
        }
    }

    private static string GetShortName(string fileName) => fileName.ToLowerInvariant() switch
    {
        "zh-hans.xml" => "HanS",
        "zh-hant.xml" => "HanT",
        "ko-kr.xml" => "KR",
        "en-us.xml" => "EN",
        _ => fileName
    };

}
