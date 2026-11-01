using System.Text;
using BF6Optimizer.Core.Common;

namespace BF6Optimizer.Infrastructure.Storage;

public static class JsonFile
{
    public static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? Json.Deserialize<T>(File.ReadAllText(path)) : null;

    /// <summary>Writes through a temp file so a crash cannot leave half a JSON document.</summary>
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
            {
                fs.Write(new UTF8Encoding(false).GetBytes(Json.Serialize(value)));
                fs.Flush(true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
