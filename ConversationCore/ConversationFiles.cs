using System.IO;
using System.Text.Json;
namespace alpha_island.Conversations;
public interface IChatSettings { string GetText(string key, string fallback); void SetValue(string key, object value); }
internal static class ConversationFiles {
 internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "alpha-island");
 internal static string Secret => Path.Combine(Root,"config","ai","secrets.dat");
 internal static string Sessions => Path.Combine(Root,"ai","sessions");
 internal static readonly JsonSerializerOptions JsonOptions=new() { WriteIndented=true, Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
