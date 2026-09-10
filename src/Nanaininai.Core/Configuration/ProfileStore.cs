using System.Text.Json;
using System.Text.Json.Serialization;
using Nanaininai.Core.Models;

namespace Nanaininai.Core.Configuration;

/// <summary>
/// Loads/saves editable LAN profiles. The bundled SCHOOL-18-PC example is a
/// default only — nothing in the engine hard-codes these values.
/// </summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public string FilePath { get; }
    public List<LanProfile> Profiles { get; private set; }

    public ProfileStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Nanaininai", "profiles.json");
        Profiles = Load();
        if (Profiles.Count == 0)
        {
            Profiles.Add(DefaultSchoolProfile());
            Save();
        }
    }

    public static LanProfile DefaultSchoolProfile() => new()
    {
        Name = "SCHOOL-18-PC",
        NetworkCidr = "192.168.10.0/24",
        Gateway = "192.168.10.1",
        StartIp = "192.168.10.10",
        PcCount = 18,
        NamePrefix = "SCHOOL-PC",
        NamePadding = 2,
        ReservedIps = new List<string> { "192.168.10.2", "192.168.10.3" },
        PreferredDns = "192.168.10.1",
        AlternateDns = "8.8.8.8"
    };

    private List<LanProfile> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<LanProfile>();
            var list = JsonSerializer.Deserialize<List<LanProfile>>(File.ReadAllText(FilePath), JsonOpts);
            return list ?? new List<LanProfile>();
        }
        catch
        {
            return new List<LanProfile>();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Profiles, JsonOpts));
        }
        catch
        {
            // Read-only environments keep profiles in memory only.
        }
    }

    public LanProfile? Find(string name) => Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
