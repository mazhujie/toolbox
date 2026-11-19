using System;
using System.IO;
using System.Security;
using System.Web.Script.Serialization;

internal sealed class UiSettings
{
    public int Width = 1100, Height = 820;
    public string View = "speed";

    internal static string DefaultPath
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Toolbox", "ui-settings.json"); }
    }

    internal static UiSettings Load(string path)
    {
        try { return new JavaScriptSerializer().Deserialize<UiSettings>(File.ReadAllText(path)) ?? new UiSettings(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return new UiSettings();
    }

    internal void Save(string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(this));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (SecurityException) { }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }
}
