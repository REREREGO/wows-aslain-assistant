using System.Drawing;
using System.Reflection;

namespace AslainAssistant
{
    public static class Brand
    {
        public static Icon LoadIcon()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppIcon"))
                return stream == null ? (Icon)SystemIcons.Application.Clone() : new Icon(stream);
        }
        public static Image LoadLogo()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AppLogo"))
            using (var image = Image.FromStream(stream)) return new Bitmap(image);
        }
    }
}
