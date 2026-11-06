// MausMakro – eigener Makro-Editor für die Glorious Model D Wireless.
//
// Makros werden aus Schritten zusammengestellt (Maustasten – auch mehrere gleichzeitig –, Tastatur, Warten),
// direkt in die Maus geschrieben und auf eine Taste gelegt. Glorious Core wird dafür nicht gebraucht.
//
// Protokoll (aus Glorious Core, MouseV2ProDeviceHandler): Feature-Reports (Report-ID 0, 65 Bytes) an die
// Herstellerschnittstelle (VID 258A, PID 2012 Kabel / 2023 Empfänger, Usage Page FFFF). Die Maus antwortet A1 = OK.
//   Makro löschen:   00 00 00 02 02 04 02 idHi idLo
//   Länge setzen:    00 00 00 02 06 04 01 idHi idLo 00 00 lenHi lenLo
//   Daten (Seite):   00 00 00 02 3A 04 03 idHi idLo 00 00 offHi offLo 33 <51 Bytes>
//   Taste belegen:   00 00 00 02 0A 03 00 Profil TastenID 00 <Funktion>
//                    Makro: Einmal 10 03 idHi idLo 01 · Solange gedrückt 11 02 idHi idLo 00 · An/Aus 12 02 idHi idLo 00
// Makro-Ereignisse: [1, Maske] Maustasten (1 L, 2 R, 4 M, 8 Zurück, 16 Vor), [2, HID]/[3, HID] Taste drücken/loslassen,
// [9, Bit]/[10, Bit] Strg/Umschalt/Alt/Win drücken/loslassen, [32, ms] Pause 1–255 ms.
//
// Build: build.cmd
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

namespace MausMakro
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            try { SetProcessDPIAware(); }
            catch (EntryPointNotFoundException) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var g = Graphics.FromHwnd(IntPtr.Zero))
                Ui.Scale = g.DpiX / 96f;
            Application.Run(new MainForm());
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
    }

    static class Ui
    {
        public static float Scale = 1f;
        public static int S(int px) { return (int)Math.Round(px * Scale); }
    }

    // ===================================================================== Datenmodell

    public enum StepKind { Mouse = 0, KeyTap = 1, KeyDown = 2, KeyUp = 3, Wait = 4 }

    public class Step
    {
        public StepKind Kind { get; set; }
        public int Mask { get; set; }   // Mouse: gedrückte Maustasten als Bitmaske
        public int Key { get; set; }    // Tastatur: HID-Usage (Strg/Umschalt/Alt/Win: 0xE0–0xE7)
        public int Ms { get; set; }     // Wait: Dauer, KeyTap: Haltedauer

        public Step Clone() { return (Step)MemberwiseClone(); }
    }

    public enum MacroMode { Once = 0, WhileHeld = 1, Toggle = 2 }

    public class MacroDef
    {
        public string Name { get; set; }
        public MacroMode Mode { get; set; }
        public string Button { get; set; }  // ButtonInfo.Id oder null
        public List<Step> Steps { get; set; }

        public MacroDef()
        {
            Name = "Neues Makro";
            Mode = MacroMode.Toggle;
            Steps = new List<Step>();
        }

        public override string ToString()
        {
            var button = ButtonInfo.Find(Button);
            string name = string.IsNullOrWhiteSpace(Name) ? "(ohne Namen)" : Name;
            return button == null ? name : name + "   [" + button.ShortName + "]";
        }
    }

    public class SaveFile
    {
        public int Version { get; set; }
        public int Profile { get; set; }  // 0 = automatisch (wie in Glorious Core), sonst 1–3
        public List<MacroDef> Macros { get; set; }

        public SaveFile()
        {
            Version = 1;
            Macros = new List<MacroDef>();
        }
    }

    public sealed class ButtonInfo
    {
        public readonly string Id, Name, ShortName;
        public readonly byte HidId;          // Tasten-ID im Belegungspaket
        public readonly int Slot;            // Makro-Platz auf der Maus (fest je Taste)
        public readonly byte[] DefaultBinding;

        ButtonInfo(string id, string name, string shortName, byte hidId, int slot, byte[] defaultBinding)
        {
            Id = id; Name = name; ShortName = shortName; HidId = hidId; Slot = slot; DefaultBinding = defaultBinding;
        }

        public static readonly ButtonInfo[] All =
        {
            new ButtonInfo("Forward", "Vordere Seitentaste", "vorne", 5, 1, new byte[] { 1, 1, 5 }),
            new ButtonInfo("Back", "Hintere Seitentaste", "hinten", 4, 2, new byte[] { 1, 1, 4 }),
            new ButtonInfo("Middle", "Mausrad-Klick", "Mausrad", 3, 3, new byte[] { 1, 1, 3 }),
            new ButtonInfo("Dpi", "DPI-Taste", "DPI", 20, 4, new byte[] { 7, 1, 6 }),
        };

        public static ButtonInfo Find(string id)
        {
            return All.FirstOrDefault(b => b.Id == id);
        }

        public static int IndexOf(string id)
        {
            for (int i = 0; i < All.Length; i++)
                if (All[i].Id == id)
                    return i;
            return -1;
        }
    }

    // ===================================================================== Tasten

    public sealed class KeyInfo
    {
        public readonly int Hid, Scan;
        public readonly bool Ext;
        public readonly string Name;

        public KeyInfo(int hid, int scan, bool ext, string name)
        {
            Hid = hid; Scan = scan; Ext = ext; Name = name;
        }

        public bool IsModifier { get { return Hid >= 0xE0 && Hid <= 0xE7; } }
        public byte ModifierBit { get { return (byte)(1 << (Hid - 0xE0)); } }
        public override string ToString() { return Name; }
    }

    static class KeyTable
    {
        // HID-Codes sind positionsbezogen. Zeichentasten bekommen ihren Namen vom aktuellen Tastaturlayout,
        // damit z. B. auf deutscher Tastatur „Z“ und „Ü“ richtig angezeigt werden.
        public static readonly List<KeyInfo> All = Build();

        static List<KeyInfo> Build()
        {
            var letters = new List<KeyInfo>();
            int[] letterScans = { 0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
                                  0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C };
            for (int i = 0; i < 26; i++)
                letters.Add(Make(0x04 + i, letterScans[i], false, null));

            var list = new List<KeyInfo>(letters.OrderBy(k => k.Name, StringComparer.CurrentCulture));
            for (int i = 0; i < 10; i++)
                list.Add(Make(0x1E + i, 0x02 + i, false, null));                     // 1 … 9, 0
            for (int i = 0; i < 10; i++)
                list.Add(Make(0x3A + i, 0x3B + i, false, "F" + (i + 1)));             // F1 … F10
            list.Add(Make(0x44, 0x57, false, "F11"));
            list.Add(Make(0x45, 0x58, false, "F12"));

            list.Add(Make(0x2C, 0x39, false, "Leertaste"));
            list.Add(Make(0x28, 0x1C, false, "Eingabe"));
            list.Add(Make(0x29, 0x01, false, "Esc"));
            list.Add(Make(0x2B, 0x0F, false, "Tab"));
            list.Add(Make(0x2A, 0x0E, false, "Rücktaste"));
            list.Add(Make(0x39, 0x3A, false, "Feststelltaste"));
            list.Add(Make(0x49, 0x52, true, "Einfg"));
            list.Add(Make(0x4C, 0x53, true, "Entf"));
            list.Add(Make(0x4A, 0x47, true, "Pos1"));
            list.Add(Make(0x4D, 0x4F, true, "Ende"));
            list.Add(Make(0x4B, 0x49, true, "Bild auf"));
            list.Add(Make(0x4E, 0x51, true, "Bild ab"));
            list.Add(Make(0x52, 0x48, true, "Pfeil oben"));
            list.Add(Make(0x51, 0x50, true, "Pfeil unten"));
            list.Add(Make(0x50, 0x4B, true, "Pfeil links"));
            list.Add(Make(0x4F, 0x4D, true, "Pfeil rechts"));
            list.Add(Make(0x46, 0x37, true, "Druck"));
            list.Add(Make(0x47, 0x46, false, "Rollen"));
            list.Add(Make(0x48, 0x45, false, "Pause"));
            list.Add(Make(0x65, 0x5D, true, "Menütaste"));

            int[,] punctuation = { { 0x2D, 0x0C }, { 0x2E, 0x0D }, { 0x2F, 0x1A }, { 0x30, 0x1B }, { 0x31, 0x2B }, { 0x33, 0x27 },
                                   { 0x34, 0x28 }, { 0x35, 0x29 }, { 0x36, 0x33 }, { 0x37, 0x34 }, { 0x38, 0x35 }, { 0x64, 0x56 } };
            for (int i = 0; i < punctuation.GetLength(0); i++)
                list.Add(Make(punctuation[i, 0], punctuation[i, 1], false, null));

            list.Add(Make(0x53, 0x45, true, "Num-Taste"));
            int[] numScans = { 0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49, 0x52 };
            for (int i = 0; i < 10; i++)
                list.Add(Make(0x59 + i, numScans[i], false, "Num " + ((i + 1) % 10)));
            list.Add(Make(0x54, 0x35, true, "Num /"));
            list.Add(Make(0x55, 0x37, false, "Num *"));
            list.Add(Make(0x56, 0x4A, false, "Num -"));
            list.Add(Make(0x57, 0x4E, false, "Num +"));
            list.Add(Make(0x63, 0x53, false, "Num ,"));
            list.Add(Make(0x58, 0x1C, true, "Num Eingabe"));

            list.Add(Make(0xE1, 0x2A, false, "Umschalt links"));
            list.Add(Make(0xE5, 0x36, false, "Umschalt rechts"));
            list.Add(Make(0xE0, 0x1D, false, "Strg links"));
            list.Add(Make(0xE4, 0x1D, true, "Strg rechts"));
            list.Add(Make(0xE2, 0x38, false, "Alt"));
            list.Add(Make(0xE6, 0x38, true, "Alt Gr"));
            list.Add(Make(0xE3, 0x5B, true, "Windows links"));
            list.Add(Make(0xE7, 0x5C, true, "Windows rechts"));
            return list;
        }

        static KeyInfo Make(int hid, int scan, bool ext, string name)
        {
            if (name == null)
            {
                var sb = new StringBuilder(64);
                int lParam = (scan << 16) | (ext ? 1 << 24 : 0);
                if (GetKeyNameText(lParam, sb, sb.Capacity) > 0)
                    name = sb.ToString();
                if (string.IsNullOrWhiteSpace(name))
                    name = "Taste " + hid.ToString("X2");
                else if (name.Length > 1)
                    name = name.Length == 1 ? name.ToUpper() : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name.ToLower());
            }
            return new KeyInfo(hid, scan, ext, name);
        }

        public static KeyInfo ByHid(int hid)
        {
            return All.FirstOrDefault(k => k.Hid == hid);
        }

        public static KeyInfo ByScan(int scan, bool ext)
        {
            return All.FirstOrDefault(k => k.Scan == scan && k.Ext == ext);
        }

        public static string NameOf(int hid)
        {
            var key = ByHid(hid);
            return key != null ? key.Name : "(keine Taste)";
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetKeyNameText(int lParam, StringBuilder buffer, int size);
    }

    // ===================================================================== Übersetzung in Firmware-Ereignisse

    sealed class CompiledMacro
    {
        public byte[] Data;
        public int DurationMs;
        public bool ReleaseAdded;
    }

    static class MacroCompiler
    {
        const int MaxPause = 250;   // längere Pausen werden gestückelt (Opcode 33 gilt als unzuverlässig)
        public const int MinTapMs = 10;

        public static CompiledMacro Compile(MacroDef macro)
        {
            var bytes = new List<byte>();
            var held = new List<KeyInfo>();
            int mask = 0, time = 0;
            bool lastWasEvent = false;

            // Glorious Core setzt zwischen zwei Ereignisse immer eine Pause (mind. 1 ms) – das machen wir genauso.
            Action<byte, byte> emit = (op, value) =>
            {
                if (lastWasEvent)
                {
                    bytes.Add(32); bytes.Add(1);
                    time += 1;
                }
                bytes.Add(op); bytes.Add(value);
                lastWasEvent = true;
            };
            // Während Maustasten gehalten werden, wird ihr Zustand alle 250 ms erneut gesendet.
            Action<int> wait = ms =>
            {
                while (ms > 0)
                {
                    int chunk = Math.Min(MaxPause, ms);
                    bytes.Add(32); bytes.Add((byte)chunk);
                    time += chunk;
                    ms -= chunk;
                    lastWasEvent = false;
                    if (ms > 0 && mask != 0)
                    {
                        bytes.Add(1); bytes.Add((byte)mask);
                        lastWasEvent = true;
                    }
                }
            };
            Action<KeyInfo> keyDown = k =>
            {
                if (k.IsModifier) emit(9, k.ModifierBit);
                else emit(2, (byte)k.Hid);
                if (!held.Contains(k))
                    held.Add(k);
            };
            Action<KeyInfo> keyUp = k =>
            {
                if (k.IsModifier) emit(10, k.ModifierBit);
                else emit(3, (byte)k.Hid);
                held.Remove(k);
            };

            foreach (var step in macro.Steps)
            {
                KeyInfo key = KeyTable.ByHid(step.Key);
                switch (step.Kind)
                {
                    case StepKind.Mouse:
                        mask = step.Mask & 0x1F;
                        emit(1, (byte)mask);
                        break;
                    case StepKind.KeyTap:
                        if (key == null) break;
                        keyDown(key);
                        wait(Math.Max(step.Ms, MinTapMs));
                        keyUp(key);
                        break;
                    case StepKind.KeyDown:
                        if (key != null) keyDown(key);
                        break;
                    case StepKind.KeyUp:
                        if (key != null) keyUp(key);
                        break;
                    case StepKind.Wait:
                        wait(step.Ms);
                        break;
                }
            }

            // Die Firmware behält nach dem Stoppen den letzten Zustand bei – daher am Ende alles loslassen.
            bool released = held.Count > 0 || mask != 0;
            foreach (var key in held.ToList())
                keyUp(key);
            if (mask != 0)
            {
                mask = 0;
                emit(1, 0);
            }
            return new CompiledMacro { Data = bytes.ToArray(), DurationMs = time, ReleaseAdded = released };
        }

        public static int StepDuration(Step step)
        {
            if (step.Kind == StepKind.Wait) return Math.Max(step.Ms, 0);
            if (step.Kind == StepKind.KeyTap) return Math.Max(step.Ms, MinTapMs);
            return 0;
        }
    }

    // ===================================================================== Texte

    static class Texts
    {
        public static readonly string[] MouseButtonNames =
            { "Links", "Rechts", "Mitte (Mausrad)", "Zurück (hintere Seitentaste)", "Vor (vordere Seitentaste)" };
        static readonly string[] MouseButtonShort = { "Links", "Rechts", "Mitte", "Zurück", "Vor" };

        public static readonly string[] ModeNames =
            { "Einmal abspielen", "Wiederholen, solange die Taste gedrückt ist", "An/Aus: erster Druck startet, zweiter stoppt" };
        public static readonly string[] ModeShort = { "einmal", "solange gedrückt", "An/Aus" };

        public static string Mask(int mask)
        {
            var parts = new List<string>();
            for (int i = 0; i < MouseButtonShort.Length; i++)
                if ((mask & (1 << i)) != 0)
                    parts.Add(MouseButtonShort[i]);
            return string.Join(" + ", parts);
        }

        public static string StepText(Step step)
        {
            switch (step.Kind)
            {
                case StepKind.Mouse:
                    return step.Mask == 0 ? "Maustasten: alle loslassen" : "Maustasten gedrückt: " + Mask(step.Mask);
                case StepKind.KeyTap:
                    return "Taste antippen: " + KeyTable.NameOf(step.Key) + "  (" + step.Ms + " ms gedrückt)";
                case StepKind.KeyDown:
                    return "Taste drücken und halten: " + KeyTable.NameOf(step.Key);
                case StepKind.KeyUp:
                    return "Taste loslassen: " + KeyTable.NameOf(step.Key);
                case StepKind.Wait:
                    return "Warten: " + Duration(step.Ms);
            }
            return "?";
        }

        public static string Duration(int ms)
        {
            if (ms < 1000)
                return ms + " ms";
            return (ms / 1000.0).ToString("0.###", CultureInfo.CurrentCulture) + " s";
        }

        public static string Seconds(int ms)
        {
            return (ms / 1000.0).ToString("0.000", CultureInfo.CurrentCulture) + " s";
        }

        public static string KindTitle(StepKind kind)
        {
            switch (kind)
            {
                case StepKind.Mouse: return "Maustasten";
                case StepKind.KeyTap: return "Taste antippen";
                case StepKind.KeyDown: return "Taste drücken und halten";
                case StepKind.KeyUp: return "Taste loslassen";
                default: return "Warten";
            }
        }
    }

    static class Templates
    {
        public static readonly string[] Names =
        {
            "LMB+RMB halten (An/Aus)",
            "LMB+RMB halten + alle 5 s F (An/Aus)",
            "Dauerklick links, 10× pro Sekunde (solange gedrückt)",
        };

        public static MacroDef Create(int index)
        {
            var m = new MacroDef();
            switch (index)
            {
                case 0:
                    m.Name = "LMB+RMB halten";
                    m.Mode = MacroMode.Toggle;
                    m.Steps.Add(new Step { Kind = StepKind.Mouse, Mask = 3 });
                    m.Steps.Add(new Step { Kind = StepKind.Wait, Ms = 3000 });
                    break;
                case 1:
                    m.Name = "LMB+RMB halten + alle 5 s F";
                    m.Mode = MacroMode.Toggle;
                    m.Steps.Add(new Step { Kind = StepKind.Mouse, Mask = 3 });
                    m.Steps.Add(new Step { Kind = StepKind.Wait, Ms = 4750 });
                    m.Steps.Add(new Step { Kind = StepKind.KeyTap, Key = 0x09, Ms = 100 });
                    m.Steps.Add(new Step { Kind = StepKind.Wait, Ms = 150 });
                    break;
                default:
                    m.Name = "Dauerklick links";
                    m.Mode = MacroMode.WhileHeld;
                    m.Steps.Add(new Step { Kind = StepKind.Mouse, Mask = 1 });
                    m.Steps.Add(new Step { Kind = StepKind.Wait, Ms = 30 });
                    m.Steps.Add(new Step { Kind = StepKind.Mouse, Mask = 0 });
                    m.Steps.Add(new Step { Kind = StepKind.Wait, Ms = 70 });
                    break;
            }
            return m;
        }
    }

    // ===================================================================== Glorious Core (nur lesen)

    static class GloriousCore
    {
        static string DataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glorious Core", "datastore"); }
        }

        public static bool IsRunning()
        {
            try { return Process.GetProcessesByName("Glorious Core").Length > 0; }
            catch (InvalidOperationException) { return false; }
        }

        // Aktives Profil der Model D Wireless laut Glorious Core (1–3), 0 wenn unbekannt.
        public static int ActiveProfile()
        {
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var devices = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(DataDir, "Devices.json")));
                var device = Items(devices["deviceInstances"]).FirstOrDefault(d => (d["productId"] as string) == "ModelDWireless");
                if (device == null)
                    return 0;
                string profileId = device["currentDeviceProfileId"] as string;
                var profiles = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(DataDir, "DeviceProfiles.json")));
                var profile = Items(profiles["profiles"]).FirstOrDefault(p => (p["id"] as string) == profileId);
                if (profile == null || !profile.ContainsKey("order"))
                    return 0;
                int order = Convert.ToInt32(profile["order"]);
                return order >= 0 && order < 3 ? order + 1 : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        static IEnumerable<IDictionary<string, object>> Items(object array)
        {
            return ((IEnumerable)array).OfType<IDictionary<string, object>>();
        }
    }

    // ===================================================================== Maus (HID)

    sealed class MouseDevice : IDisposable
    {
        const ushort Vid = 0x258A, PidCable = 0x2012, PidReceiver = 0x2023;
        const int PageSize = 51;

        readonly SafeFileHandle handle;
        readonly int featureLength;
        public readonly string Connection;

        MouseDevice(Hid.Info info)
        {
            handle = Hid.TryOpen(info.Path, true);
            if (handle == null)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Die Maus-Schnittstelle konnte nicht geöffnet werden");
            featureLength = info.FeatureLength;
            Connection = info.Pid == PidCable ? "Kabel" : "Funk-Empfänger";
        }

        public static Hid.Info FindTarget()
        {
            return Hid.FindInterfaces(Vid, new[] { PidCable, PidReceiver })
                .Where(h => h.UsagePage == 0xFFFF && h.Usage == 0 && h.Writable && h.FeatureLength > 0)
                .OrderBy(h => h.Pid == PidCable ? 0 : 1)
                .FirstOrDefault();
        }

        public static MouseDevice Open()
        {
            var target = FindTarget();
            if (target == null)
                throw new IOException("Maus nicht gefunden – ist die Model D Wireless per Kabel oder Funk-Empfänger verbunden?");
            return new MouseDevice(target);
        }

        public void WriteMacro(int slot, byte[] data)
        {
            byte hi = (byte)(slot >> 8), lo = (byte)slot;
            Send(new byte[] { 0, 0, 0, 2, 2, 4, 2, hi, lo }, 80);
            Send(new byte[] { 0, 0, 0, 2, 6, 4, 1, hi, lo, 0, 0, (byte)(data.Length >> 8), (byte)data.Length }, 80);
            for (int offset = 0; offset < data.Length; offset += PageSize)
            {
                var page = new byte[65];
                page[3] = 2;
                page[4] = PageSize + 7;
                page[5] = 4;
                page[6] = 3;
                page[7] = hi;
                page[8] = lo;
                page[11] = (byte)(offset >> 8);
                page[12] = (byte)offset;
                page[13] = PageSize;
                Array.Copy(data, offset, page, 14, Math.Min(PageSize, data.Length - offset));
                Send(page, 80);
            }
        }

        public void BindMacro(int profile, ButtonInfo button, MacroMode mode, int slot)
        {
            byte hi = (byte)(slot >> 8), lo = (byte)slot;
            byte[] function;
            switch (mode)
            {
                case MacroMode.Once: function = new byte[] { 16, 3, hi, lo, 1 }; break;
                case MacroMode.WhileHeld: function = new byte[] { 17, 2, hi, lo, 0 }; break;
                default: function = new byte[] { 18, 2, hi, lo, 0 }; break;
            }
            SendBinding(profile, button.HidId, function);
        }

        public void ResetBinding(int profile, ButtonInfo button)
        {
            SendBinding(profile, button.HidId, button.DefaultBinding);
        }

        void SendBinding(int profile, byte buttonId, byte[] function)
        {
            var packet = new byte[65];
            packet[3] = 2;
            packet[4] = 10;
            packet[5] = 3;
            packet[6] = 0;
            packet[7] = (byte)profile;
            packet[8] = buttonId;
            packet[9] = 0;
            Array.Copy(function, 0, packet, 10, function.Length);
            Send(packet, 100);
        }

        // Senden und Status abfragen wie Glorious Core (SetAndCheckStatus): A2 = erneut senden, A0 = erneut abfragen.
        void Send(byte[] packet, int delayMs)
        {
            var report = new byte[featureLength];
            Array.Copy(packet, report, Math.Min(packet.Length, report.Length));
            report[0] = 0;

            bool send = true;
            string error = "keine Bestätigung";
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (send && !HidD_SetFeature(handle, report, report.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Senden an die Maus fehlgeschlagen");
                send = false;
                Thread.Sleep(attempt == 0 ? delayMs : 40);

                var answer = new byte[featureLength];
                if (!HidD_GetFeature(handle, answer, answer.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Keine Antwort von der Maus");
                byte status = answer[0] >= 0xA0 && answer[0] <= 0xA4 ? answer[0] : answer[1];
                if (status == 0xA2) { error = "Firmware-Befehl fehlgeschlagen (A2)"; send = true; continue; }
                if (status == 0xA0) { error = "Firmware beschäftigt (A0)"; continue; }
                if (status == 0xA4) { error = "Empfänger findet die Maus nicht – ist sie eingeschaltet? (A4)"; continue; }
                return;
            }
            throw new IOException(error);
        }

        public void Dispose()
        {
            handle.Dispose();
        }

        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);
    }

    static class Hid
    {
        public sealed class Info
        {
            public string Path;
            public ushort Pid, UsagePage, Usage, FeatureLength;
            public bool Writable;
        }

        public static List<Info> FindInterfaces(ushort vid, ushort[] pids)
        {
            var result = new List<Info>();
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr devs = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (devs == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA();
                ifData.cbSize = Marshal.SizeOf(ifData);
                string vidText = "vid_" + vid.ToString("x4");
                for (uint i = 0; SetupDiEnumDeviceInterfaces(devs, IntPtr.Zero, ref hidGuid, i, ref ifData); i++)
                {
                    string path = GetInterfacePath(devs, ref ifData);
                    if (path == null || path.IndexOf(vidText, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var hid = new Info { Path = path };
                    SafeFileHandle handle = TryOpen(path, true);
                    hid.Writable = handle != null;
                    if (handle == null)
                        handle = TryOpen(path, false); // Maus-/Tastatur-Schnittstellen hält Windows exklusiv
                    if (handle == null)
                        continue;
                    using (handle)
                    {
                        var attr = new HIDD_ATTRIBUTES();
                        attr.Size = Marshal.SizeOf(attr);
                        if (!HidD_GetAttributes(handle, ref attr) || attr.VendorID != vid || !pids.Contains(attr.ProductID))
                            continue;
                        hid.Pid = attr.ProductID;

                        IntPtr preparsed;
                        if (HidD_GetPreparsedData(handle, out preparsed))
                        {
                            try
                            {
                                HIDP_CAPS caps;
                                if (HidP_GetCaps(preparsed, out caps) == HIDP_STATUS_SUCCESS)
                                {
                                    hid.UsagePage = caps.UsagePage;
                                    hid.Usage = caps.Usage;
                                    hid.FeatureLength = caps.FeatureReportByteLength;
                                }
                            }
                            finally
                            {
                                HidD_FreePreparsedData(preparsed);
                            }
                        }
                    }
                    result.Add(hid);
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devs);
            }
            return result;
        }

        static string GetInterfacePath(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA ifData)
        {
            uint required;
            SetupDiGetDeviceInterfaceDetail(devs, ref ifData, IntPtr.Zero, 0, out required, IntPtr.Zero);
            if (required == 0)
                return null;
            IntPtr detail = Marshal.AllocHGlobal((int)required);
            try
            {
                Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6); // cbSize von SP_DEVICE_INTERFACE_DETAIL_DATA_W
                if (!SetupDiGetDeviceInterfaceDetail(devs, ref ifData, detail, required, out required, IntPtr.Zero))
                    return null;
                return Marshal.PtrToStringUni(detail + 4);
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }

        public static SafeFileHandle TryOpen(string path, bool readWrite)
        {
            var handle = CreateFile(path, readWrite ? GENERIC_READ | GENERIC_WRITE : 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            return handle;
        }

        const uint DIGCF_PRESENT = 0x02, DIGCF_DEVICEINTERFACE = 0x10;
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        const int HIDP_STATUS_SUCCESS = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID, ProductID, VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct HIDP_CAPS
        {
            public ushort Usage, UsagePage;
            public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
            public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        static extern void HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetAttributes(SafeFileHandle device, ref HIDD_ATTRIBUTES attributes);
        [DllImport("hid.dll", SetLastError = true)]
        static extern bool HidD_GetPreparsedData(SafeFileHandle device, out IntPtr preparsedData);
        [DllImport("hid.dll")]
        static extern bool HidD_FreePreparsedData(IntPtr preparsedData);
        [DllImport("hid.dll")]
        static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

        [DllImport("setupapi.dll", SetLastError = true)]
        static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        static extern bool SetupDiEnumDeviceInterfaces(IntPtr devs, IntPtr devInfo, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA interfaceData);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devs, ref SP_DEVICE_INTERFACE_DATA interfaceData, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr devInfo);
        [DllImport("setupapi.dll")]
        static extern bool SetupDiDestroyDeviceInfoList(IntPtr devs);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    }

    // ===================================================================== Hauptfenster

    sealed class MainForm : Form
    {
        readonly string savePath;
        SaveFile data;
        MacroDef current;
        bool loading, busy, checkingDevice;

        ListBox lstMacros;
        TextBox txtName, txtLog;
        ComboBox cboMode, cboButton, cboProfile, cboResetButton;
        ListView lvSteps;
        Label lblInfo, lblDevice;
        TableLayoutPanel editor;
        readonly List<Control> mouseControls = new List<Control>();
        readonly System.Windows.Forms.Timer deviceTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        readonly System.Windows.Forms.Timer recordTimer = new System.Windows.Forms.Timer { Interval = 250 };
        Recorder recorder;
        Button btnRecord;

        public const string Version = "1.1";

        public MainForm()
        {
            Text = "MausMakro " + Version + " – Glorious Model D Wireless";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(Ui.S(1080), Ui.S(780));
            MinimumSize = new Size(Ui.S(900), Ui.S(700));
            savePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MausMakro", "makros.json");

            BuildUi();
            LoadData();
            FillProfiles();
            RefreshMacroList(data.Macros.Count > 0 ? 0 : -1);

            Log("Makros werden automatisch gespeichert: " + savePath);
            if (GloriousCore.IsRunning())
                Log("Hinweis: Glorious Core läuft. Ändert man dort Tastenbelegungen, überschreibt es die von hier – danach einfach „Alle zugewiesenen schreiben“.");

            deviceTimer.Tick += (s, e) => CheckDevice();
            deviceTimer.Start();
            CheckDevice();
        }

        // ------------------------------------------------------------- Aufbau

        void BuildUi()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(Ui.S(8)) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Ui.S(330)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, Ui.S(130)));
            Controls.Add(root);

            // --- links: Makroliste
            var left = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, Ui.S(10), 0) };
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Controls.Add(Heading("Makros"), 0, 0);
            lstMacros = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            lstMacros.SelectedIndexChanged += (s, e) => { if (!loading) ShowMacro(lstMacros.SelectedItem as MacroDef); };
            left.Controls.Add(lstMacros, 0, 1);

            var templateMenu = new ContextMenuStrip();
            for (int i = 0; i < Templates.Names.Length; i++)
            {
                int index = i;
                templateMenu.Items.Add(Templates.Names[i], null, (s, e) => AddMacro(Templates.Create(index)));
            }
            Button btnTemplate = null;
            btnTemplate = MakeButton("Vorlage ▾", (s, e) => templateMenu.Show(btnTemplate, new Point(0, btnTemplate.Height)));
            var leftButtons = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 1, Margin = new Padding(0, Ui.S(4), 0, 0) };
            var listButtons = new[]
            {
                MakeButton("Neu", (s, e) => AddMacro(new MacroDef())),
                btnTemplate,
                MakeButton("Kopieren", (s, e) => CopyMacro()),
                MakeButton("Löschen", (s, e) => DeleteMacro()),
            };
            for (int i = 0; i < listButtons.Length; i++)
            {
                leftButtons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                int height = listButtons[i].PreferredSize.Height;
                listButtons[i].AutoSize = false;
                listButtons[i].MinimumSize = Size.Empty;
                listButtons[i].Height = height;
                listButtons[i].Anchor = AnchorStyles.Left | AnchorStyles.Right;
                listButtons[i].Margin = new Padding(i == 0 ? 0 : Ui.S(2), 0, i == listButtons.Length - 1 ? 0 : Ui.S(2), 0);
                leftButtons.Controls.Add(listButtons[i], i, 0);
            }
            left.Controls.Add(leftButtons, 0, 2);
            root.Controls.Add(left, 0, 0);

            // --- rechts: Editor
            editor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = new Padding(0) };
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var props = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4, RowCount = 2, Margin = new Padding(0) };
            props.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            props.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            props.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            props.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            props.Controls.Add(FieldLabel("Name:"), 0, 0);
            txtName = new TextBox { Dock = DockStyle.Fill };
            txtName.TextChanged += (s, e) => OnNameChanged();
            props.Controls.Add(txtName, 1, 0);
            props.SetColumnSpan(txtName, 3);
            props.Controls.Add(FieldLabel("Modus:"), 0, 1);
            cboMode = Combo();
            cboMode.Dock = DockStyle.Fill;
            cboMode.Items.AddRange(Texts.ModeNames);
            cboMode.SelectedIndexChanged += (s, e) => OnModeChanged();
            props.Controls.Add(cboMode, 1, 1);
            props.Controls.Add(FieldLabel("Taste:"), 2, 1);
            cboButton = Combo();
            cboButton.Dock = DockStyle.Fill;
            cboButton.Items.Add("– keine –");
            foreach (var b in ButtonInfo.All)
                cboButton.Items.Add(b.Name);
            cboButton.SelectedIndexChanged += (s, e) => OnButtonChanged();
            props.Controls.Add(cboButton, 3, 1);
            editor.Controls.Add(props, 0, 0);

            editor.Controls.Add(new Label
            {
                Text = "Schritte – werden von oben nach unten abgespielt:",
                AutoSize = true,
                Margin = new Padding(Ui.S(3), Ui.S(12), Ui.S(3), Ui.S(4))
            }, 0, 1);

            var stepsArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            stepsArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            stepsArea.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            lvSteps = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            lvSteps.Columns.Add("#", Ui.S(40));
            lvSteps.Columns.Add("Schritt", Ui.S(400));
            lvSteps.Columns.Add("Start bei", Ui.S(90), HorizontalAlignment.Right);
            lvSteps.DoubleClick += (s, e) => EditStep();
            lvSteps.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Delete) DeleteSteps();
                else if (e.KeyCode == Keys.Enter) EditStep();
            };
            lvSteps.Resize += (s, e) => FitColumns();
            stepsArea.Controls.Add(lvSteps, 0, 0);

            var stepButtons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(Ui.S(8), 0, 0, 0)
            };
            btnRecord = StepButton("● Aufnehmen", (s, e) => ToggleRecording());
            btnRecord.Font = new Font(Font, FontStyle.Bold);
            stepButtons.Controls.Add(btnRecord);
            recordTimer.Tick += (s, e) =>
            {
                if (recorder != null)
                    lblInfo.Text = "● Aufnahme läuft – " + recorder.Count + " Ereignisse aufgenommen. Beenden mit F12 oder „Stopp“.";
            };
            stepButtons.Controls.Add(new Label { Height = Ui.S(8), AutoSize = false });
            stepButtons.Controls.Add(StepButton("+ Maustasten …", (s, e) => AddStep(StepKind.Mouse)));
            stepButtons.Controls.Add(StepButton("+ Taste antippen …", (s, e) => AddStep(StepKind.KeyTap)));
            stepButtons.Controls.Add(StepButton("+ Taste drücken …", (s, e) => AddStep(StepKind.KeyDown)));
            stepButtons.Controls.Add(StepButton("+ Taste loslassen …", (s, e) => AddStep(StepKind.KeyUp)));
            stepButtons.Controls.Add(StepButton("+ Warten …", (s, e) => AddStep(StepKind.Wait)));
            stepButtons.Controls.Add(new Label { Height = Ui.S(8), AutoSize = false });
            stepButtons.Controls.Add(StepButton("Bearbeiten …", (s, e) => EditStep()));
            stepButtons.Controls.Add(StepButton("Duplizieren", (s, e) => DuplicateSteps()));
            stepButtons.Controls.Add(StepButton("Löschen", (s, e) => DeleteSteps()));
            stepButtons.Controls.Add(StepButton("Nach oben ▲", (s, e) => MoveStep(-1)));
            stepButtons.Controls.Add(StepButton("Nach unten ▼", (s, e) => MoveStep(1)));
            stepsArea.Controls.Add(stepButtons, 1, 0);
            editor.Controls.Add(stepsArea, 0, 2);

            lblInfo = new Label { AutoSize = true, Margin = new Padding(Ui.S(3), Ui.S(8), Ui.S(3), Ui.S(3)) };
            editor.Controls.Add(lblInfo, 0, 3);
            editor.Resize += (s, e) => lblInfo.MaximumSize = new Size(Math.Max(Ui.S(200), editor.Width - Ui.S(10)), 0);
            root.Controls.Add(editor, 1, 0);

            // --- unten: Maus
            var bar = Flow();
            bar.Margin = new Padding(0, Ui.S(10), 0, Ui.S(6));
            lblDevice = FieldLabel("Maus: …");
            lblDevice.Font = new Font(Font, FontStyle.Bold);
            lblDevice.MinimumSize = new Size(Ui.S(250), 0);
            bar.Controls.Add(lblDevice);
            bar.Controls.Add(FieldLabel("Profil:"));
            cboProfile = Combo();
            cboProfile.Width = Ui.S(200);
            cboProfile.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                data.Profile = cboProfile.SelectedIndex;
                Save();
            };
            bar.Controls.Add(cboProfile);
            var btnWrite = MakeButton("Dieses Makro auf die Maus schreiben", (s, e) =>
            {
                if (current == null) Log("Kein Makro ausgewählt.");
                else WriteMacros(new List<MacroDef> { current });
            });
            btnWrite.Font = new Font(Font, FontStyle.Bold);
            var btnAll = MakeButton("Alle zugewiesenen schreiben", (s, e) =>
            {
                var assigned = data.Macros.Where(m => ButtonInfo.Find(m.Button) != null).ToList();
                if (assigned.Count == 0) Log("Keinem Makro ist eine Taste zugewiesen.");
                else WriteMacros(assigned);
            });
            bar.Controls.Add(btnWrite);
            bar.Controls.Add(btnAll);
            bar.SetFlowBreak(btnAll, true);
            bar.Controls.Add(FieldLabel("Taste auf Standard zurücksetzen:"));
            cboResetButton = Combo();
            cboResetButton.Width = Ui.S(170);
            foreach (var b in ButtonInfo.All)
                cboResetButton.Items.Add(b.Name);
            cboResetButton.SelectedIndex = 0;
            bar.Controls.Add(cboResetButton);
            var btnReset = MakeButton("Zurücksetzen", (s, e) => ResetButton());
            bar.Controls.Add(btnReset);
            var btnDump = MakeButton("Firmware-Daten ins Protokoll", (s, e) => DumpBytes());
            btnDump.Margin = new Padding(Ui.S(24), Ui.S(3), Ui.S(3), Ui.S(3));
            bar.Controls.Add(btnDump);
            mouseControls.AddRange(new Control[] { cboProfile, btnWrite, btnAll, cboResetButton, btnReset });
            root.Controls.Add(bar, 0, 1);
            root.SetColumnSpan(bar, 2);

            txtLog = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = SystemColors.Window,
                Font = new Font("Consolas", 9f)
            };
            root.Controls.Add(txtLog, 0, 2);
            root.SetColumnSpan(txtLog, 2);
        }

        static Label Heading(string text)
        {
            var label = new Label { Text = text, AutoSize = true, Margin = new Padding(Ui.S(1), Ui.S(2), Ui.S(3), Ui.S(6)) };
            label.Font = new Font(SystemFonts.MessageBoxFont.FontFamily, SystemFonts.MessageBoxFont.Size + 2, FontStyle.Bold);
            return label;
        }

        static Label FieldLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(6), Ui.S(3)) };
        }

        static FlowLayoutPanel Flow()
        {
            return new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        }

        static ComboBox Combo()
        {
            return new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Anchor = AnchorStyles.Left };
        }

        static Button MakeButton(string text, EventHandler click)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(Ui.S(60), 0),
                Padding = new Padding(Ui.S(4), Ui.S(1), Ui.S(4), Ui.S(1)),
                UseVisualStyleBackColor = true
            };
            if (click != null)
                button.Click += click;
            return button;
        }

        static Button StepButton(string text, EventHandler click)
        {
            var button = MakeButton(text, click);
            button.MinimumSize = new Size(Ui.S(175), 0);
            button.TextAlign = ContentAlignment.MiddleLeft;
            button.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            button.Margin = new Padding(0, 0, 0, Ui.S(4));
            return button;
        }

        void FitColumns()
        {
            int width = lvSteps.ClientSize.Width - lvSteps.Columns[0].Width - lvSteps.Columns[2].Width - 2;
            if (width > Ui.S(100))
                lvSteps.Columns[1].Width = width;
        }

        // ------------------------------------------------------------- Daten

        void LoadData()
        {
            data = null;
            if (File.Exists(savePath))
            {
                try
                {
                    data = new JavaScriptSerializer().Deserialize<SaveFile>(File.ReadAllText(savePath, Encoding.UTF8));
                }
                catch (Exception ex)
                {
                    string broken = savePath + ".defekt";
                    try { File.Copy(savePath, broken, true); }
                    catch (IOException) { }
                    Log("Makro-Datei konnte nicht gelesen werden (" + ex.Message + "). Kopie: " + broken);
                }
            }
            if (data == null)
            {
                // Erster Start: Beispielmakros anlegen (noch keiner Taste zugewiesen).
                data = new SaveFile();
                for (int i = 0; i < Templates.Names.Length; i++)
                    data.Macros.Add(Templates.Create(i));
                Save();
            }
            if (data.Macros == null)
                data.Macros = new List<MacroDef>();
            foreach (var m in data.Macros)
                if (m.Steps == null)
                    m.Steps = new List<Step>();
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(savePath));
                string temp = savePath + ".tmp";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(data), Encoding.UTF8);
                if (File.Exists(savePath))
                    File.Replace(temp, savePath, null);
                else
                    File.Move(temp, savePath);
            }
            catch (Exception ex)
            {
                Log("Speichern fehlgeschlagen: " + ex.Message);
            }
        }

        // ------------------------------------------------------------- Makroliste

        void RefreshMacroList(int selectIndex)
        {
            loading = true;
            lstMacros.BeginUpdate();
            lstMacros.Items.Clear();
            foreach (var m in data.Macros)
                lstMacros.Items.Add(m);
            lstMacros.EndUpdate();
            if (selectIndex >= lstMacros.Items.Count)
                selectIndex = lstMacros.Items.Count - 1;
            lstMacros.SelectedIndex = selectIndex;
            loading = false;
            ShowMacro(lstMacros.SelectedItem as MacroDef);
        }

        void RefreshMacroTexts()
        {
            loading = true;
            for (int i = 0; i < lstMacros.Items.Count; i++)
                lstMacros.Items[i] = lstMacros.Items[i];
            loading = false;
        }

        void AddMacro(MacroDef macro)
        {
            data.Macros.Add(macro);
            Save();
            RefreshMacroList(data.Macros.Count - 1);
            txtName.Focus();
            txtName.SelectAll();
        }

        void CopyMacro()
        {
            if (current == null) return;
            var serializer = new JavaScriptSerializer();
            var copy = serializer.Deserialize<MacroDef>(serializer.Serialize(current));
            copy.Name = current.Name + " (Kopie)";
            copy.Button = null;
            AddMacro(copy);
        }

        void DeleteMacro()
        {
            if (current == null) return;
            if (MessageBox.Show(this, "Makro „" + current.Name + "“ löschen?\n\nAuf der Maus bleibt es gespeichert, bis die Taste neu belegt wird.",
                    "Makro löschen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            int index = data.Macros.IndexOf(current);
            data.Macros.Remove(current);
            Save();
            RefreshMacroList(Math.Max(0, index - 1));
        }

        void ShowMacro(MacroDef macro)
        {
            current = macro;
            loading = true;
            editor.Enabled = macro != null;
            txtName.Text = macro != null ? macro.Name : "";
            cboMode.SelectedIndex = macro != null ? (int)macro.Mode : -1;
            cboButton.SelectedIndex = macro != null ? ButtonInfo.IndexOf(macro.Button) + 1 : -1;
            loading = false;
            RefreshSteps(-1);
        }

        void OnNameChanged()
        {
            if (loading || current == null) return;
            current.Name = txtName.Text;
            Save();
            RefreshMacroTexts();
        }

        void OnModeChanged()
        {
            if (loading || current == null || cboMode.SelectedIndex < 0) return;
            current.Mode = (MacroMode)cboMode.SelectedIndex;
            Save();
            UpdateInfo();
        }

        void OnButtonChanged()
        {
            if (loading || current == null) return;
            int index = cboButton.SelectedIndex;
            string id = index <= 0 ? null : ButtonInfo.All[index - 1].Id;
            if (id != null)
            {
                foreach (var other in data.Macros)
                {
                    if (other != current && other.Button == id)
                    {
                        other.Button = null;
                        Log("„" + other.Name + "“ ist jetzt keiner Taste mehr zugewiesen.");
                    }
                }
            }
            current.Button = id;
            Save();
            RefreshMacroTexts();
            UpdateInfo();
        }

        // ------------------------------------------------------------- Schritte

        void RefreshSteps(int selectIndex)
        {
            lvSteps.BeginUpdate();
            lvSteps.Items.Clear();
            if (current != null)
            {
                int time = 0;
                for (int i = 0; i < current.Steps.Count; i++)
                {
                    var step = current.Steps[i];
                    var item = new ListViewItem((i + 1).ToString());
                    item.SubItems.Add(Texts.StepText(step));
                    item.SubItems.Add(Texts.Seconds(time));
                    lvSteps.Items.Add(item);
                    time += MacroCompiler.StepDuration(step);
                }
            }
            lvSteps.EndUpdate();
            if (selectIndex >= 0 && selectIndex < lvSteps.Items.Count)
            {
                lvSteps.Items[selectIndex].Selected = true;
                lvSteps.Items[selectIndex].Focused = true;
                lvSteps.EnsureVisible(selectIndex);
            }
            UpdateInfo();
        }

        void UpdateInfo()
        {
            if (current == null)
            {
                lblInfo.Text = data.Macros.Count == 0 ? "Noch keine Makros – links auf „Neu“ oder „Vorlage“ klicken." : "";
                return;
            }
            if (current.Steps.Count == 0)
            {
                lblInfo.Text = "Noch keine Schritte – rechts oben einen Schritt hinzufügen.";
                return;
            }
            var compiled = MacroCompiler.Compile(current);
            var text = new StringBuilder();
            text.Append("Ein Durchlauf dauert ").Append(Texts.Duration(compiled.DurationMs))
                .Append(" · ").Append(compiled.Data.Length).Append(" Bytes auf der Maus. ");
            switch (current.Mode)
            {
                case MacroMode.Once: text.Append("Läuft einmal pro Tastendruck."); break;
                case MacroMode.WhileHeld: text.Append("Wiederholt sich, solange die Taste gedrückt ist."); break;
                default: text.Append("Erster Druck startet die Schleife, zweiter Druck stoppt sie nach dem laufenden Durchlauf."); break;
            }
            if (compiled.ReleaseAdded)
                text.Append(" Am Ende jedes Durchlaufs werden alle noch gedrückten Tasten automatisch losgelassen.");
            if (current.Mode != MacroMode.Once && compiled.DurationMs < 20)
                text.Append("\nAchtung: ohne Wartezeit wiederholt sich das Makro extrem schnell.");
            if (compiled.Data.Length > 1000)
                text.Append("\nAchtung: sehr langes Makro – ungetestet, ob die Maus so viel speichert.");
            if (ButtonInfo.Find(current.Button) == null)
                text.Append("\nNoch keiner Taste zugewiesen (oben bei „Taste“).");
            lblInfo.Text = text.ToString();
        }

        List<int> SelectedStepIndices()
        {
            return lvSteps.SelectedIndices.Cast<int>().OrderBy(i => i).ToList();
        }

        void AddStep(StepKind kind)
        {
            if (current == null) return;
            var selected = SelectedStepIndices();
            int insertAt = selected.Count > 0 ? selected.Last() + 1 : current.Steps.Count;

            var step = new Step { Kind = kind };
            if (kind == StepKind.Wait) step.Ms = 1000;
            if (kind == StepKind.KeyTap) step.Ms = 50;
            if (kind == StepKind.KeyUp)
            {
                // Vorschlag: die zuletzt gedrückte Taste
                for (int i = insertAt - 1; i >= 0; i--)
                {
                    if (current.Steps[i].Kind == StepKind.KeyDown)
                    {
                        step.Key = current.Steps[i].Key;
                        break;
                    }
                }
            }
            using (var dialog = new StepDialog(step))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
            }
            current.Steps.Insert(insertAt, step);
            Save();
            RefreshSteps(insertAt);
        }

        void EditStep()
        {
            if (current == null) return;
            var selected = SelectedStepIndices();
            if (selected.Count == 0) return;
            int index = selected[0];
            var copy = current.Steps[index].Clone();
            using (var dialog = new StepDialog(copy))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
            }
            current.Steps[index] = copy;
            Save();
            RefreshSteps(index);
        }

        void DuplicateSteps()
        {
            if (current == null) return;
            var selected = SelectedStepIndices();
            if (selected.Count == 0) return;
            int insertAt = selected.Last() + 1;
            current.Steps.InsertRange(insertAt, selected.Select(i => current.Steps[i].Clone()).ToList());
            Save();
            RefreshSteps(insertAt);
        }

        void DeleteSteps()
        {
            if (current == null) return;
            var selected = SelectedStepIndices();
            if (selected.Count == 0) return;
            for (int i = selected.Count - 1; i >= 0; i--)
                current.Steps.RemoveAt(selected[i]);
            Save();
            RefreshSteps(Math.Min(selected[0], current.Steps.Count - 1));
        }

        void MoveStep(int direction)
        {
            if (current == null) return;
            var selected = SelectedStepIndices();
            if (selected.Count != 1) return;
            int from = selected[0], to = from + direction;
            if (to < 0 || to >= current.Steps.Count) return;
            var step = current.Steps[from];
            current.Steps.RemoveAt(from);
            current.Steps.Insert(to, step);
            Save();
            RefreshSteps(to);
        }

        void DumpBytes()
        {
            if (current == null || current.Steps.Count == 0) return;
            var bytes = MacroCompiler.Compile(current).Data;
            Log("Firmware-Daten „" + current.Name + "“ (" + bytes.Length + " Bytes):");
            for (int offset = 0; offset < bytes.Length; offset += 32)
            {
                var line = bytes.Skip(offset).Take(32).Select(b => b.ToString("X2"));
                Log("    " + string.Join(" ", line));
            }
        }

        // ------------------------------------------------------------- Maus

        void FillProfiles()
        {
            int fromCore = GloriousCore.ActiveProfile();
            loading = true;
            cboProfile.Items.Clear();
            cboProfile.Items.Add(fromCore > 0 ? "Automatisch (jetzt Profil " + fromCore + ")" : "Automatisch (Profil 1)");
            for (int i = 1; i <= 3; i++)
                cboProfile.Items.Add("Profil " + i);
            cboProfile.SelectedIndex = data.Profile >= 1 && data.Profile <= 3 ? data.Profile : 0;
            loading = false;
        }

        int EffectiveProfile()
        {
            if (data.Profile >= 1 && data.Profile <= 3)
                return data.Profile;
            int fromCore = GloriousCore.ActiveProfile();
            return fromCore > 0 ? fromCore : 1;
        }

        async void CheckDevice()
        {
            if (busy || checkingDevice) return;
            checkingDevice = true;
            try
            {
                var target = await Task.Run(() => MouseDevice.FindTarget());
                if (target == null)
                {
                    lblDevice.Text = "Maus: nicht gefunden";
                    lblDevice.ForeColor = Color.Firebrick;
                }
                else
                {
                    lblDevice.Text = "Maus: verbunden (" + (target.Pid == 0x2012 ? "Kabel" : "Funk-Empfänger") + ")";
                    lblDevice.ForeColor = Color.ForestGreen;
                }
            }
            catch (Exception ex)
            {
                lblDevice.Text = "Maus: Fehler";
                lblDevice.ForeColor = Color.Firebrick;
                Log("Gerätesuche fehlgeschlagen: " + ex.Message);
            }
            finally
            {
                checkingDevice = false;
            }
        }

        sealed class WriteJob
        {
            public string Name;
            public ButtonInfo Button;
            public MacroMode Mode;
            public byte[] Data;
        }

        async void WriteMacros(List<MacroDef> macros)
        {
            if (busy) return;
            var jobs = new List<WriteJob>();
            foreach (var m in macros)
            {
                var button = ButtonInfo.Find(m.Button);
                if (button == null)
                {
                    Log("„" + m.Name + "“ ist keiner Taste zugewiesen – bitte oben bei „Taste“ eine auswählen.");
                    return;
                }
                var compiled = MacroCompiler.Compile(m);
                if (compiled.Data.Length == 0)
                {
                    Log("„" + m.Name + "“ hat keine Schritte.");
                    return;
                }
                jobs.Add(new WriteJob { Name = m.Name, Button = button, Mode = m.Mode, Data = compiled.Data });
            }

            int profile = EffectiveProfile();
            SetBusy(true);
            try
            {
                await Task.Run(() =>
                {
                    using (var device = MouseDevice.Open())
                    {
                        Log("Schreibe über " + device.Connection + " in Profil " + profile + " …");
                        foreach (var job in jobs)
                        {
                            device.WriteMacro(job.Button.Slot, job.Data);
                            device.BindMacro(profile, job.Button, job.Mode, job.Button.Slot);
                            Log("OK: „" + job.Name + "“ → " + job.Button.Name + " (" + Texts.ModeShort[(int)job.Mode] + ", "
                                + job.Data.Length + " Bytes, Makro-Platz " + job.Button.Slot + ")");
                        }
                    }
                });
                Log("Fertig – die Maus hat alles bestätigt.");
            }
            catch (Exception ex)
            {
                Log("Fehler: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        async void ResetButton()
        {
            if (busy || cboResetButton.SelectedIndex < 0) return;
            var button = ButtonInfo.All[cboResetButton.SelectedIndex];
            int profile = EffectiveProfile();
            SetBusy(true);
            try
            {
                await Task.Run(() =>
                {
                    using (var device = MouseDevice.Open())
                        device.ResetBinding(profile, button);
                });
                Log(button.Name + " hat wieder ihre Standardfunktion (Profil " + profile + ").");
                bool changed = false;
                foreach (var m in data.Macros.Where(x => x.Button == button.Id))
                {
                    m.Button = null;
                    changed = true;
                    Log("„" + m.Name + "“ ist jetzt keiner Taste mehr zugewiesen.");
                }
                if (changed)
                {
                    Save();
                    RefreshMacroTexts();
                    ShowMacro(current);
                }
            }
            catch (Exception ex)
            {
                Log("Fehler: " + ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ------------------------------------------------------------- Aufnahme

        void ToggleRecording()
        {
            if (recorder == null)
                StartRecording();
            else
                StopRecording(true);
        }

        void StartRecording()
        {
            if (current == null) return;
            var rec = new Recorder();
            rec.StopRequested += () => BeginInvoke(new Action(() => StopRecording(false)));
            try
            {
                rec.Start();
            }
            catch (Exception ex)
            {
                Log("Aufnahme konnte nicht gestartet werden: " + ex.Message);
                return;
            }
            recorder = rec;
            btnRecord.Text = "■ Stopp (F12)";
            btnRecord.ForeColor = Color.Firebrick;
            recordTimer.Start();
            Log("Aufnahme läuft – jetzt Tasten und Maustasten drücken (auch in anderen Fenstern). "
                + "F12 oder „Stopp“ beendet die Aufnahme. Mausbewegungen und Mausrad werden nicht aufgenommen.");
        }

        void StopRecording(bool byButton)
        {
            if (recorder == null) return;
            // Wurde per Klick auf „Stopp“ beendet, gehört dieser letzte Linksklick nicht zur Aufnahme.
            var events = recorder.Stop(byButton);
            recorder = null;
            recordTimer.Stop();
            btnRecord.Text = "● Aufnehmen";
            btnRecord.ForeColor = SystemColors.ControlText;
            UpdateInfo();

            if (events.Count == 0)
            {
                Log("Aufnahme beendet – es wurde nichts aufgenommen.");
                return;
            }
            using (var dialog = new RecordDialog(events, current != null))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    Log("Aufnahme verworfen.");
                    return;
                }
                ApplyRecording(dialog.Result, dialog.Target);
            }
        }

        void ApplyRecording(List<Step> steps, RecordTarget target)
        {
            if (target == RecordTarget.NewMacro || current == null)
            {
                var macro = new MacroDef { Name = "Aufnahme " + DateTime.Now.ToString("HH:mm"), Mode = MacroMode.Once };
                macro.Steps.AddRange(steps);
                AddMacro(macro);
            }
            else if (target == RecordTarget.Replace)
            {
                current.Steps.Clear();
                current.Steps.AddRange(steps);
                Save();
                RefreshSteps(-1);
            }
            else
            {
                var selected = SelectedStepIndices();
                int insertAt = selected.Count > 0 ? selected.Last() + 1 : current.Steps.Count;
                current.Steps.InsertRange(insertAt, steps);
                Save();
                RefreshSteps(insertAt);
            }
            Log("Aufnahme übernommen: " + steps.Count + " Schritte.");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (recorder != null)
            {
                recorder.Stop(false);
                recorder = null;
            }
            base.OnFormClosing(e);
        }

        void SetBusy(bool value)
        {
            busy = value;
            foreach (var control in mouseControls)
                control.Enabled = !value;
            UseWaitCursor = value;
        }

        void Log(string text)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Log), text);
                return;
            }
            txtLog.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        }
    }

    // ===================================================================== Aufnahme

    public sealed class RecordedEvent
    {
        public uint Time;       // Millisekunden (Systemzeit des Ereignisses)
        public StepKind Kind;   // KeyDown, KeyUp oder Mouse
        public int Key;         // HID-Usage
        public int Mask;        // Maustasten-Zustand nach dem Ereignis
    }

    // Zeichnet Tastatur und Maustasten systemweit über Low-Level-Hooks auf. Künstlich erzeugte Eingaben
    // (SendInput) werden ignoriert, Mausbewegungen und Mausrad ebenfalls. F12 beendet die Aufnahme.
    sealed class Recorder
    {
        const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        const uint LLKHF_EXTENDED = 0x01, LLKHF_INJECTED = 0x10, LLKHF_UP = 0x80, LLMHF_INJECTED = 0x01;
        public const int StopVirtualKey = 0x7B; // F12

        readonly List<RecordedEvent> events = new List<RecordedEvent>();
        readonly HashSet<int> keysDown = new HashSet<int>();
        readonly HookProc keyboardProc, mouseProc; // Referenzen halten, sonst sammelt der GC die Delegates ein
        IntPtr keyboardHook, mouseHook;
        int mask;
        int lastLeftDown = -1;

        public event Action StopRequested;

        public Recorder()
        {
            keyboardProc = KeyboardHook;
            mouseProc = MouseHook;
        }

        public int Count { get { return events.Count; } }

        public void Start()
        {
            IntPtr module = GetModuleHandle(null);
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (keyboardHook == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            if (mouseHook == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                Unhook();
                throw new Win32Exception(error);
            }
        }

        public List<RecordedEvent> Stop(bool dropLastLeftClick)
        {
            Unhook();
            if (dropLastLeftClick && lastLeftDown >= 0 && lastLeftDown < events.Count)
                events.RemoveRange(lastLeftDown, events.Count - lastLeftDown);
            return events;
        }

        void Unhook()
        {
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            keyboardHook = mouseHook = IntPtr.Zero;
        }

        // Gibt true zurück, wenn die Taste verschluckt werden soll (Stopp-Taste).
        internal bool OnKey(int virtualKey, int scanCode, bool extended, bool up, bool injected, uint time)
        {
            if (virtualKey == StopVirtualKey)
            {
                if (!up)
                {
                    var handler = StopRequested;
                    if (handler != null)
                        handler();
                }
                return true;
            }
            if (injected)
                return false;
            // Der von Alt Gr zusätzlich erzeugte Strg-Druck hat Scancode 0x21D und wird hier nicht gefunden.
            var key = KeyTable.ByScan(scanCode, extended);
            if (key == null)
                return false;
            if (up)
            {
                if (keysDown.Remove(key.Hid))
                    events.Add(new RecordedEvent { Time = time, Kind = StepKind.KeyUp, Key = key.Hid, Mask = mask });
            }
            else if (keysDown.Add(key.Hid)) // gedrückt gehaltene Tasten wiederholen KeyDown – nur das erste zählt
            {
                events.Add(new RecordedEvent { Time = time, Kind = StepKind.KeyDown, Key = key.Hid, Mask = mask });
            }
            return false;
        }

        internal void OnMouse(int message, uint mouseData, bool injected, uint time)
        {
            if (injected)
                return;
            int bit;
            bool down;
            switch (message)
            {
                case 0x201: bit = 1; down = true; break;   // WM_LBUTTONDOWN
                case 0x202: bit = 1; down = false; break;
                case 0x204: bit = 2; down = true; break;   // WM_RBUTTONDOWN
                case 0x205: bit = 2; down = false; break;
                case 0x207: bit = 4; down = true; break;   // WM_MBUTTONDOWN
                case 0x208: bit = 4; down = false; break;
                case 0x20B:                                // WM_XBUTTONDOWN
                case 0x20C:
                    int x = (int)(mouseData >> 16);
                    bit = x == 1 ? 8 : x == 2 ? 16 : 0;
                    down = message == 0x20B;
                    break;
                default:
                    return;
            }
            if (bit == 0)
                return;
            int newMask = down ? mask | bit : mask & ~bit;
            if (newMask == mask)
                return;
            mask = newMask;
            if (bit == 1 && down)
                lastLeftDown = events.Count;
            events.Add(new RecordedEvent { Time = time, Kind = StepKind.Mouse, Mask = mask });
        }

        IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var info = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool swallow = OnKey((int)info.vkCode, (int)info.scanCode, (info.flags & LLKHF_EXTENDED) != 0,
                    (info.flags & LLKHF_UP) != 0, (info.flags & LLKHF_INJECTED) != 0, info.time);
                if (swallow)
                    return new IntPtr(1);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0)
            {
                var info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                OnMouse(wParam.ToInt32(), info.mouseData, (info.flags & LLMHF_INJECTED) != 0, info.time);
            }
            return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        }

        delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode, scanCode, flags, time;
            public IntPtr extraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public int x, y;
            public uint mouseData, flags, time;
            public IntPtr extraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
    }

    public enum PauseMode { AsRecorded = 0, Rounded = 1, Fixed = 2 }
    public enum RecordTarget { Insert = 0, Replace = 1, NewMacro = 2 }

    static class RecordConverter
    {
        const int MaxWaitPerStep = 60000;
        const int MaxTapMs = 10000;

        public static List<Step> ToSteps(List<RecordedEvent> events, PauseMode pauses, int fixedMs)
        {
            var steps = new List<Step>();
            for (int i = 0; i < events.Count; i++)
            {
                var e = events[i];
                if (i > 0)
                {
                    int delta = unchecked((int)(e.Time - events[i - 1].Time));
                    if (delta > 0)
                        steps.Add(new Step { Kind = StepKind.Wait, Ms = delta });
                }
                if (e.Kind == StepKind.Mouse)
                    steps.Add(new Step { Kind = StepKind.Mouse, Mask = e.Mask });
                else
                    steps.Add(new Step { Kind = e.Kind, Key = e.Key });
            }

            // Drücken + (Warten) + Loslassen derselben Taste → „Taste antippen“
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i].Kind != StepKind.KeyDown)
                    continue;
                int key = steps[i].Key;
                if (i + 1 < steps.Count && steps[i + 1].Kind == StepKind.KeyUp && steps[i + 1].Key == key)
                {
                    steps[i] = new Step { Kind = StepKind.KeyTap, Key = key, Ms = MacroCompiler.MinTapMs };
                    steps.RemoveAt(i + 1);
                }
                else if (i + 2 < steps.Count && steps[i + 1].Kind == StepKind.Wait && steps[i + 1].Ms <= MaxTapMs
                         && steps[i + 2].Kind == StepKind.KeyUp && steps[i + 2].Key == key)
                {
                    steps[i] = new Step { Kind = StepKind.KeyTap, Key = key, Ms = Math.Max(MacroCompiler.MinTapMs, steps[i + 1].Ms) };
                    steps.RemoveRange(i + 1, 2);
                }
            }

            var result = new List<Step>();
            foreach (var step in steps)
            {
                if (step.Kind == StepKind.KeyTap && pauses != PauseMode.AsRecorded)
                    step.Ms = Math.Max(MacroCompiler.MinTapMs, RoundTo(step.Ms, 10));
                if (step.Kind != StepKind.Wait)
                {
                    result.Add(step);
                    continue;
                }
                int ms = step.Ms;
                if (pauses == PauseMode.Rounded) ms = RoundTo(ms, 50);
                else if (pauses == PauseMode.Fixed) ms = fixedMs;
                while (ms > 0)
                {
                    int chunk = Math.Min(ms, MaxWaitPerStep);
                    result.Add(new Step { Kind = StepKind.Wait, Ms = chunk });
                    ms -= chunk;
                }
            }
            return result;
        }

        static int RoundTo(int value, int step)
        {
            return (int)Math.Round(value / (double)step, MidpointRounding.AwayFromZero) * step;
        }
    }

    sealed class RecordDialog : Form
    {
        readonly List<RecordedEvent> events;
        readonly RadioButton optAsRecorded, optRounded, optFixed, optInsert, optReplace, optNew;
        readonly NumericUpDown fixedMs;
        readonly Label preview;

        public List<Step> Result { get; private set; }
        public RecordTarget Target { get; private set; }

        public RecordDialog(List<RecordedEvent> events, bool hasMacro)
        {
            this.events = events;
            Text = "Aufnahme übernehmen";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            int margin = Ui.S(14);
            var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Location = new Point(margin, margin) };
            int duration = unchecked((int)(events[events.Count - 1].Time - events[0].Time));
            table.Controls.Add(new Label
            {
                Text = events.Count + " Ereignisse aufgenommen, Dauer " + Texts.Duration(Math.Max(0, duration)) + ".",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(3), Ui.S(10))
            });

            table.Controls.Add(SectionLabel("Pausen zwischen den Schritten:"));
            optAsRecorded = Radio("genau wie aufgenommen", true);
            optRounded = Radio("auf 50 ms runden (übersichtlicher)", false);
            optFixed = Radio("alle gleich lang:", false);
            fixedMs = new NumericUpDown { Minimum = 1, Maximum = 60000, Value = 100, Increment = 50, Width = Ui.S(90), ThousandsSeparator = true };
            var fixedRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            fixedRow.Controls.Add(optFixed);
            fixedRow.Controls.Add(fixedMs);
            fixedRow.Controls.Add(new Label { Text = "ms", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(4), Ui.S(3), 0, 0) });
            var pausePanel = Group(optAsRecorded, optRounded, fixedRow);
            table.Controls.Add(pausePanel);

            table.Controls.Add(SectionLabel("Wohin damit?"));
            optInsert = Radio("in das aktuelle Makro einfügen (nach dem markierten Schritt)", hasMacro);
            optReplace = Radio("alle Schritte des aktuellen Makros ersetzen", false);
            optNew = Radio("als neues Makro anlegen", !hasMacro);
            optInsert.Enabled = optReplace.Enabled = hasMacro;
            table.Controls.Add(Group(optInsert, optReplace, optNew));

            preview = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(Ui.S(3), Ui.S(10), Ui.S(3), Ui.S(3)) };
            table.Controls.Add(preview);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Ui.S(12), 0, 0) };
            var cancel = new Button { Text = "Verwerfen", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Ui.S(90), 0) };
            var ok = new Button { Text = "Übernehmen", AutoSize = true, MinimumSize = new Size(Ui.S(90), 0) };
            ok.Click += (s, e) => Accept();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            table.Controls.Add(buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            // „alle gleich lang“ steckt in einer eigenen Zeile – die drei Optionen daher von Hand gruppieren.
            var pauseOptions = new[] { optAsRecorded, optRounded, optFixed };
            foreach (var radio in pauseOptions)
            {
                var self = radio;
                self.CheckedChanged += (s, e) =>
                {
                    if (!self.Checked) return;
                    foreach (var other in pauseOptions)
                        if (other != self)
                            other.Checked = false;
                    UpdatePreview();
                };
            }
            fixedMs.ValueChanged += (s, e) => { optFixed.Checked = true; UpdatePreview(); };
            UpdatePreview();

            Controls.Add(table);
            var size = table.GetPreferredSize(Size.Empty);
            ClientSize = new Size(size.Width + 2 * margin, size.Height + 2 * margin);
        }

        static Label SectionLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Margin = new Padding(Ui.S(3), Ui.S(6), Ui.S(3), Ui.S(2)) };
        }

        static RadioButton Radio(string text, bool isChecked)
        {
            return new RadioButton { Text = text, AutoSize = true, Checked = isChecked, Anchor = AnchorStyles.Left };
        }

        // Jede Gruppe in ein eigenes Panel, damit die Optionsfelder unabhängig voneinander sind.
        static Control Group(params Control[] controls)
        {
            var panel = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(Ui.S(16), 0, Ui.S(3), 0)
            };
            panel.Controls.AddRange(controls);
            return panel;
        }

        PauseMode SelectedPauses
        {
            get { return optRounded.Checked ? PauseMode.Rounded : optFixed.Checked ? PauseMode.Fixed : PauseMode.AsRecorded; }
        }

        List<Step> BuildSteps()
        {
            return RecordConverter.ToSteps(events, SelectedPauses, (int)fixedMs.Value);
        }

        void UpdatePreview()
        {
            var steps = BuildSteps();
            int duration = steps.Sum(s => MacroCompiler.StepDuration(s));
            preview.Text = "Ergibt " + steps.Count + " Schritte, ein Durchlauf dauert ca. " + Texts.Duration(duration) + ".";
        }

        void Accept()
        {
            Result = BuildSteps();
            Target = optReplace.Checked ? RecordTarget.Replace : optNew.Checked ? RecordTarget.NewMacro : RecordTarget.Insert;
            DialogResult = DialogResult.OK;
        }
    }

    // ===================================================================== Schritt-Dialog

    sealed class StepDialog : Form
    {
        readonly Step step;
        readonly CheckBox[] checks = new CheckBox[5];
        ComboBox cboKey;
        KeyCaptureBox capture;
        NumericUpDown number;
        Label numberHint;

        public StepDialog(Step step)
        {
            this.step = step;
            Text = Texts.KindTitle(step.Kind);
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;

            int margin = Ui.S(14);
            var table = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Location = new Point(margin, margin) };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            int row = 0;

            if (step.Kind == StepKind.Mouse)
            {
                AddSpanning(table, ref row, new Label
                {
                    Text = "Ab diesem Schritt sind genau diese Maustasten gedrückt.\nAlle anderen sind losgelassen – nichts ankreuzen = alle loslassen.",
                    AutoSize = true,
                    Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(3), Ui.S(8))
                });
                for (int i = 0; i < checks.Length; i++)
                {
                    checks[i] = new CheckBox { Text = Texts.MouseButtonNames[i], AutoSize = true, Checked = (step.Mask & (1 << i)) != 0 };
                    AddSpanning(table, ref row, checks[i]);
                }
            }
            else if (step.Kind == StepKind.Wait)
            {
                table.Controls.Add(Caption("Wartezeit:"), 0, row);
                number = Number(1, 60000, step.Ms > 0 ? step.Ms : 1000);
                table.Controls.Add(WithUnit(number, "ms"), 1, row++);
                numberHint = new Label { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(Ui.S(3)) };
                number.ValueChanged += (s, e) => UpdateHint();
                UpdateHint();
                table.Controls.Add(numberHint, 1, row++);
            }
            else
            {
                table.Controls.Add(Caption("Aufnehmen:"), 0, row);
                capture = new KeyCaptureBox { Width = Ui.S(300), Text = "Hier klicken, dann die Taste drücken" };
                capture.Captured += OnCaptured;
                table.Controls.Add(capture, 1, row++);
                table.Controls.Add(Caption("Taste:"), 0, row);
                cboKey = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(300), MaxDropDownItems = 20 };
                cboKey.Items.AddRange(KeyTable.All.Cast<object>().ToArray());
                var key = KeyTable.ByHid(step.Key);
                if (key != null)
                    cboKey.SelectedItem = key;
                table.Controls.Add(cboKey, 1, row++);
                if (step.Kind == StepKind.KeyTap)
                {
                    table.Controls.Add(Caption("Gedrückt für:"), 0, row);
                    number = Number(MacroCompiler.MinTapMs, 10000, step.Ms > 0 ? step.Ms : 50);
                    table.Controls.Add(WithUnit(number, "ms"), 1, row++);
                }
            }

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, Ui.S(12), 0, 0) };
            var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(Ui.S(90), 0) };
            var ok = new Button { Text = "OK", AutoSize = true, MinimumSize = new Size(Ui.S(90), 0) };
            ok.Click += (s, e) => Accept();
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            AddSpanning(table, ref row, buttons);
            AcceptButton = ok;
            CancelButton = cancel;

            Controls.Add(table);
            var size = table.GetPreferredSize(Size.Empty);
            ClientSize = new Size(size.Width + 2 * margin, size.Height + 2 * margin);
        }

        static void AddSpanning(TableLayoutPanel table, ref int row, Control control)
        {
            table.Controls.Add(control, 0, row);
            table.SetColumnSpan(control, 2);
            row++;
        }

        static Label Caption(string text)
        {
            return new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(10), Ui.S(3)) };
        }

        static NumericUpDown Number(int min, int max, int value)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Width = Ui.S(110), Increment = 50, ThousandsSeparator = true };
            n.Value = Math.Max(min, Math.Min(max, value));
            return n;
        }

        static Control WithUnit(NumericUpDown n, string unit)
        {
            var panel = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            panel.Controls.Add(n);
            panel.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(4), Ui.S(3), 0, 0) });
            return panel;
        }

        void UpdateHint()
        {
            numberHint.Text = "= " + Texts.Duration((int)number.Value) + "  (1000 ms = 1 Sekunde)";
        }

        void OnCaptured(int scan, bool ext)
        {
            var key = KeyTable.ByScan(scan, ext);
            if (key == null)
            {
                capture.Text = "Diese Taste wird nicht unterstützt";
                return;
            }
            cboKey.SelectedItem = key;
            capture.Text = key.Name + "  (übernommen)";
        }

        void Accept()
        {
            switch (step.Kind)
            {
                case StepKind.Mouse:
                    int mask = 0;
                    for (int i = 0; i < checks.Length; i++)
                        if (checks[i].Checked)
                            mask |= 1 << i;
                    step.Mask = mask;
                    break;
                case StepKind.Wait:
                    step.Ms = (int)number.Value;
                    break;
                default:
                    var key = cboKey.SelectedItem as KeyInfo;
                    if (key == null)
                    {
                        MessageBox.Show(this, "Bitte eine Taste auswählen oder aufnehmen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    step.Key = key.Hid;
                    if (step.Kind == StepKind.KeyTap)
                        step.Ms = (int)number.Value;
                    break;
            }
            DialogResult = DialogResult.OK;
        }
    }

    // Nimmt die nächste gedrückte Taste auf (inkl. Tab, Eingabe, Esc, Alt) und meldet ihren Scancode.
    sealed class KeyCaptureBox : TextBox
    {
        public event Action<int, bool> Captured;

        public KeyCaptureBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            BackColor = SystemColors.Window;
            Cursor = Cursors.Hand;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
            if (msg.Msg == WM_KEYDOWN || msg.Msg == WM_SYSKEYDOWN)
            {
                long lParam = msg.LParam.ToInt64();
                int scan = (int)((lParam >> 16) & 0xFF);
                bool ext = ((lParam >> 24) & 1) != 0;
                var handler = Captured;
                if (handler != null)
                    handler(scan, ext);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
