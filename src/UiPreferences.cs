using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AslainAssistant
{
    public sealed class UiPreferences
    {
        public bool Dark { get; set; }
        public int PageIndex { get; set; }
        public static string DefaultRoot { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WoWS-Aslain-Assistant"); } }
        public static UiPreferences Load(string root)
        {
            try
            {
                string file = Path.Combine(root,"ui.json");
                if (!File.Exists(file) || new FileInfo(file).Length > 65536) return new UiPreferences();
                var result = new JavaScriptSerializer().Deserialize<UiPreferences>(File.ReadAllText(file)) ?? new UiPreferences();
                if (result.PageIndex < 0 || result.PageIndex > 3) result.PageIndex = 0;
                return result;
            }
            catch (IOException) { return new UiPreferences(); }
            catch (UnauthorizedAccessException) { return new UiPreferences(); }
            catch (ArgumentException) { return new UiPreferences(); }
            catch (InvalidOperationException) { return new UiPreferences(); }
        }
        public void Save(string root)
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root,"ui.json"), temp = file + ".tmp";
            File.WriteAllText(temp,new JavaScriptSerializer().Serialize(this),new UTF8Encoding(true));
            if (File.Exists(file)) File.Replace(temp,file,file + ".bak",true); else File.Move(temp,file);
        }
    }
}
