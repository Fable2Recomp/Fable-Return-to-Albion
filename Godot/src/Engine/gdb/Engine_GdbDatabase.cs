using System;
using System.Collections.Generic;
using System.Text;
using Godot;

namespace Refii.Gdb;

[Tool]
[GlobalClass]
public partial class GdbDatabase : Resource
{
    public const int PreviewLimit = 200;

    private GdbView? _view;

    [Export]
    public string SourcePath { get; set; } = string.Empty;

    [Export]
    public int RecordCount { get; set; }

    [Export]
    public int NamedRecordCount { get; set; }

    [Export]
    public int DictionaryEntryCount { get; set; }

    [Export]
    public string[] Preview { get; set; } = Array.Empty<string>();

    public GdbView? View => _view;

    public static GdbDatabase? TryCreate(byte[] bytes, string sourcePath, out string error)
    {
        if (!GdbView.TryParse(bytes, out GdbView view, out error))
        {
            return null;
        }

        var database = new GdbDatabase
        {
            _view = view,
            SourcePath = sourcePath,
            RecordCount = view.RecordCount,
            DictionaryEntryCount = view.Names.Count,
        };

        int named = 0;
        for (int i = 0; i < view.RecordCount; i++)
        {
            if (view.RecordName(i).Length != 0)
            {
                named++;
            }
        }

        database.NamedRecordCount = named;

        int previewCount = Math.Min(PreviewLimit, view.RecordCount);
        var preview = new string[previewCount];
        for (int i = 0; i < previewCount; i++)
        {
            preview[i] = database.Summarise(i);
        }

        database.Preview = preview;
        return database;
    }

    public uint RecordHashAt(int index) =>
        _view is not null && index >= 0 && index < _view.RecordCount
            ? _view.RecordHash(index)
            : 0u;

    public string RecordNameAt(int index) =>
        _view is not null && index >= 0 && index < _view.RecordCount
            ? _view.RecordName(index)
            : string.Empty;

    public int IndexOfHash(uint hash) =>
        _view is not null && _view.TryLookup(hash, out int index) ? index : -1;

    public IReadOnlyList<GdbField> FieldsAt(int index) =>
        _view is not null && index >= 0 && index < _view.RecordCount
            ? _view.Fields(index)
            : Array.Empty<GdbField>();

    public string Summarise(int index)
    {
        if (_view is null || index < 0 || index >= _view.RecordCount)
        {
            return string.Empty;
        }

        string name = _view.RecordName(index);
        int fields = _view.FieldCount(index);
        string label = name.Length != 0 ? name : "(unnamed)";
        return $"0x{_view.RecordHash(index):X8}  {label}  ({fields} field{(fields == 1 ? "" : "s")})";
    }

    public string Describe(int index)
    {
        if (_view is null || index < 0 || index >= _view.RecordCount)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        text.AppendLine(Summarise(index));
        foreach (GdbField field in _view.Fields(index))
        {
            string name = field.Name.Length != 0 ? field.Name : $"0x{field.Hash:X8}";
            text.Append($"    {name} : {field.Type} = {field.Describe()}");
            if (field.Type == GdbFieldType.String &&
                Engine_BnkTextBankLoader.TryLookup(field.Raw, out string localized))
            {
                text.Append($"  → \"{Flatten(localized, 100)}\"");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Flatten(string value, int limit)
    {
        value = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return value.Length <= limit ? value : value[..limit] + "…";
    }
}
