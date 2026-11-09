using System.Text.RegularExpressions;

namespace Hhub.Core;

// Presentation only: retain the reported name for diagnostics and tooltips.
public static class HardwareDisplayName
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static string Format(string key, string reported)
    {
        if (key is not ("cpu.name" or "gpu.name")) return reported;
        string name = Regex.Replace(reported, @"\(R\)|\(TM\)|[®™]", "", Options);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        if (key == "cpu.name")
        {
            name = Regex.Replace(name, @"^\d{1,2}(?:st|nd|rd|th) Gen (?=Intel\b)", "", Options);
            if (!Regex.IsMatch(name, @"^(?:Intel|AMD|Qualcomm|Apple)\s+\S", Options)) return reported;
            name = Regex.Replace(name, @"\s+@\s*\d+(?:\.\d+)?\s*[GM]Hz$", "", Options);
            name = Regex.Replace(name, @"\s+(?:with|w/)\s+(?:AMD\s+)?Radeon\b.*$", "", Options);
            name = Regex.Replace(name, @"\s+\d+[- ]Cores?(?: Processor)?$", "", Options);
            name = Regex.Replace(name, @"\bCPU\b\s*", "", Options);
            name = Regex.Replace(name, @"\s+Processor$", "", Options);
            // Ultra, Xeon, Ryzen, Threadripper, PRO and model suffixes remain meaningful.
            name = Regex.Replace(name, @"^Intel Core (?=i[3579]-)", "Intel ", Options);
            return name.Trim();
        }

        if (Regex.IsMatch(name, @"^(?:NVIDIA\s+)?GeForce\s+\S", Options))
            name = Regex.Replace(name, @"^(?:NVIDIA\s+)?GeForce\s+", "NVIDIA ", Options);
        else if (Regex.IsMatch(name, @"^(?:AMD\s+)?Radeon\s+(?:RX|PRO|Vega|VII|HD|R[579])\b", Options))
            name = Regex.Replace(name, @"^(?:AMD\s+)?Radeon\s+", "AMD ", Options);
        else if (!Regex.IsMatch(name, @"^(?:NVIDIA|AMD|Intel|Qualcomm|Apple)\s+\S", Options))
            return reported;

        // Remove redundant device labels only; retain Laptop/Mobile, Ti, SUPER, XT,
        // XTX, GRE, Max-Q, professional series and memory-size distinctions.
        if (!Regex.IsMatch(name, @"^(?:AMD Radeon|Intel (?:HD|UHD)) Graphics$", Options))
            name = Regex.Replace(name, @"\bGraphics\b(?=\s+\d|\s*$)", "", Options);
        name = Regex.Replace(name, @"\s+GPU$", "", Options);
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return Regex.IsMatch(name, @"^(?:NVIDIA|AMD|Intel|Qualcomm|Apple)$", Options) ? reported : name;
    }
}
