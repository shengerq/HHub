using Hhub.Core;

internal static class HardwareDisplayNameChecks
{
    public static void Run(Action<bool, string> check)
    {
        (string Key, string Raw, string Expected)[] cases =
        [
            ("gpu.name", "NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA RTX 4060 Laptop"),
            ("gpu.name", "NVIDIA GeForce RTX 4070 Ti SUPER", "NVIDIA RTX 4070 Ti SUPER"),
            ("gpu.name", "GeForce GTX 1650", "NVIDIA GTX 1650"),
            ("gpu.name", "NVIDIA GeForce RTX 2080 with Max-Q Design", "NVIDIA RTX 2080 with Max-Q Design"),
            ("gpu.name", "NVIDIA RTX A4000", "NVIDIA RTX A4000"),
            ("gpu.name", "NVIDIA Quadro P2000", "NVIDIA Quadro P2000"),
            ("gpu.name", "AMD Radeon RX 7900 XTX", "AMD RX 7900 XTX"),
            ("gpu.name", "Radeon RX 7900 GRE", "AMD RX 7900 GRE"),
            ("gpu.name", "AMD Radeon PRO W7800", "AMD PRO W7800"),
            ("gpu.name", "AMD Radeon RX 6800M", "AMD RX 6800M"),
            ("gpu.name", "AMD Radeon(TM) 780M Graphics", "AMD Radeon 780M"),
            ("gpu.name", "AMD Radeon(TM) Graphics", "AMD Radeon Graphics"),
            ("gpu.name", "Intel(R) Arc(TM) A770 Graphics", "Intel Arc A770"),
            ("gpu.name", "Intel(R) UHD Graphics 770", "Intel UHD 770"),
            ("gpu.name", "Intel(R) Iris(R) Xe Graphics", "Intel Iris Xe"),
            ("gpu.name", "Intel(R) HD Graphics", "Intel HD Graphics"),
            ("gpu.name", "Qualcomm Adreno(TM) X1-85 GPU", "Qualcomm Adreno X1-85"),
            ("gpu.name", "Apple M2", "Apple M2"),
            ("cpu.name", "Intel(R) Core(TM) i9-14900HX", "Intel i9-14900HX"),
            ("cpu.name", "12th Gen Intel(R) Core(TM) i7-12700H", "Intel i7-12700H"),
            ("cpu.name", "Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", "Intel i7-8700K"),
            ("cpu.name", "Intel(R) Core(TM) Ultra 7 155H", "Intel Core Ultra 7 155H"),
            ("cpu.name", "Intel(R) Xeon(R) CPU E5-2680 v4 @ 2.40GHz", "Intel Xeon E5-2680 v4"),
            ("cpu.name", "AMD Ryzen 7 5800X 8-Core Processor", "AMD Ryzen 7 5800X"),
            ("cpu.name", "AMD Ryzen 5 5600G with Radeon Graphics", "AMD Ryzen 5 5600G"),
            ("cpu.name", "AMD Ryzen 7 7840HS w/ Radeon 780M Graphics", "AMD Ryzen 7 7840HS"),
            ("cpu.name", "AMD Ryzen Threadripper PRO 7995WX 96-Cores", "AMD Ryzen Threadripper PRO 7995WX"),
            ("gpu.name", "Microsoft Basic Display Adapter", "Microsoft Basic Display Adapter"),
            ("gpu.name", "Unknown(TM) GPU", "Unknown(TM) GPU"),
            ("cpu.name", "Unknown(TM) Processor @ 2.40GHz", "Unknown(TM) Processor @ 2.40GHz"),
            ("gpu.name", "—", "—"),
            ("gpu.name", "", ""),
            ("cpu.load", "Intel(R) Sample", "Intel(R) Sample")
        ];
        foreach (var (key, raw, expected) in cases)
            check(HardwareDisplayName.Format(key, raw) == expected, $"hardware display name: {raw} -> {expected}");
    }
}
