using System;
using System.IO;
using System.Text.Json;

namespace Aurora386.Gui.Emulation;

/// <summary>
/// What the emulator remembers about the person using it, as opposed to
/// about a machine: for now, which machine was open last.
///
/// Kept apart from vm.json on purpose. A machine's folder describes the
/// machine and can be handed to somebody else; "the last thing you opened"
/// is about this computer and this user, and belongs where the operating
/// system keeps such things.
///
/// Nothing here may throw. Settings that cannot be read are settings that
/// do not exist, and settings that cannot be written are lost quietly: a
/// window that failed to appear because a preferences file was unreadable
/// would be the wrong trade.
/// </summary>
public sealed class UserSettings
{
    public string? LastVmFolder { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Aurora386", "settings.json");

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new UserSettings();
            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(FilePath), Options)
                   ?? new UserSettings();
        }
        catch (Exception)
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception)
        {
            // Lost, and quietly: see the class summary.
        }
    }
}
