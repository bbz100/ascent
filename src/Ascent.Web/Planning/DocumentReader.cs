using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
namespace Ascent.Web.Planning;

public sealed class UdocOptions
{
    public string Executable {get;set;}="tools/udoc/udoc.exe";
    public int TimeoutSeconds {get;set;}=30;
}
public sealed class DocumentReader(IOptions<UdocOptions>? options=null)
{
    public const long MaxBytes=10*1024*1024;
    public const int MaxCharacters=60_000;
    public const string Accept=".doc,.docx,.docm,.pdf,.rtf,.odt,.txt,.md,.csv,.ppt,.pptx,.xls,.xlsx,.ods,.odp";
    public static bool Supported(string name)=>Accept.Split(',').Contains(Path.GetExtension(name).ToLowerInvariant());
    static readonly SemaphoreSlim Workers=new(2);
    public async Task<string> ReadAsync(string name,Stream stream,CancellationToken ct=default)
    {
        if(!Supported(name))throw new PlanningException("Choose a supported document: Word, PDF, RTF, OpenDocument, text, spreadsheet or presentation.");
        using var bytes=new MemoryStream();var buffer=new byte[81920];int read;
        while((read=await stream.ReadAsync(buffer,ct))>0)
        {
            if(bytes.Length+read>MaxBytes)throw new PlanningException("Upload a file smaller than 10 MB.");
            await bytes.WriteAsync(buffer.AsMemory(0,read),ct);
        }
        bytes.Position=0;string text;
        if(new[]{".txt",".md",".csv"}.Contains(Path.GetExtension(name).ToLowerInvariant()))
        {
            using var reader=new StreamReader(bytes,new UTF8Encoding(false,true),true);
            try {text=await reader.ReadToEndAsync(ct);}
            catch(DecoderFallbackException){throw new PlanningException("Save this text file as UTF-8 or Unicode, then upload it again.");}
        }
        else text=await ExtractAsync(bytes,Path.GetExtension(name).ToLowerInvariant(),ct);
        text=text.Replace("\r\n","\n").Replace('\a','\n').Trim();
        if(text.Contains('\0'))throw new PlanningException("The document text could not be decoded reliably. No tasks were extracted. Try a DOCX or text-based PDF version; the original is retained for investigation.");
        if(string.IsNullOrWhiteSpace(text))throw new PlanningException("No readable text was found. Scanned or image-only documents require OCR; upload a text-based version.");
        if(text.Length>MaxCharacters)throw new PlanningException("This brief exceeds the 60,000 character limit. Text is never silently truncated.");
        if (Path.GetExtension(name).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) text = ReflowPdfText(text);
        return text;
    }
    async Task<string> ExtractAsync(MemoryStream bytes,string extension,CancellationToken ct)
    {
        var settings=options?.Value??new UdocOptions();
        var executable=Path.GetFullPath(settings.Executable,AppContext.BaseDirectory);
        if(!File.Exists(executable))throw new PlanningException("The document extractor is missing. Ask the administrator to install the supplied tools/udoc folder.");
        await Workers.WaitAsync(ct);
        var directory=Path.Combine(Path.GetTempPath(),"AscentDocumentRead",Guid.NewGuid().ToString("N"));
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds,1,120)));
        using var process=new Process();bool started=false;
        try
        {
            Directory.CreateDirectory(directory);
            var input=Path.Combine(directory,"input"+extension);
            await File.WriteAllBytesAsync(input,bytes.ToArray(),timeout.Token);
            process.StartInfo=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8,WorkingDirectory=directory};
            foreach(var arg in new[]{"--out","json","--no-images","--no-presentation","--max-file-size",MaxBytes.ToString()})process.StartInfo.ArgumentList.Add(arg);
            // PDF table detection emits an overlay of text also present in ordinary paragraphs.
            // Keep the ordinary text stream once. Native Word/spreadsheet tables are unchanged.
            if (extension == ".pdf") process.StartInfo.ArgumentList.Add("--no-tables");
            process.StartInfo.ArgumentList.Add(input);
            started=process.Start();
            if(!started)throw new PlanningException("The document extractor could not start.");
            var output=ReadBoundedAsync(process.StandardOutput,5_000_000,timeout.Token);
            var errors=ReadBoundedAsync(process.StandardError,32_000,timeout.Token);
            // Kill promptly if either output stream exceeds its bound; do not wait for a blocked child.
            try {await Task.WhenAll(output,errors).WaitAsync(timeout.Token);}
            catch {if(!process.HasExited)process.Kill(true);throw;}
            await process.WaitForExitAsync(timeout.Token);
            if(process.ExitCode!=0)throw new PlanningException("This document could not be extracted. It may be damaged, password-protected or use an unsupported variant. The original is retained for investigation.");
            using var json=JsonDocument.Parse(await output,new JsonDocumentOptions{MaxDepth=128});
            var text=new StringBuilder();Append(json.RootElement.GetProperty("content"),text);return text.ToString();
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){throw new PlanningException("Document extraction took too long. Try a smaller or simpler document.");}
        catch(JsonException){throw new PlanningException("The document extractor returned unreadable output.");}
        finally
        {
            try {if(started && !process.HasExited){process.Kill(true);await process.WaitForExitAsync();}}
            finally {try {if(Directory.Exists(directory))Directory.Delete(directory,true);}finally{Workers.Release();}}
        }
    }
    public static string ReflowPdfText(string text)
    {
        // PDF paragraphs often represent individual visual lines/cell fragments.
        // Bound citation chunks without interpreting or discarding their contents.
        const int target = 1000;
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > target)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
            if (current.Length > 0) current.Append(' ');
            current.Append(word);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return string.Join('\n', chunks);
    }
    static async Task<string> ReadBoundedAsync(StreamReader reader,int limit,CancellationToken ct)
    {
        var result=new StringBuilder();var buffer=new char[4096];int count;
        while((count=await reader.ReadAsync(buffer.AsMemory(),ct))>0)
        {
            if(result.Length+count>limit)throw new PlanningException("The extracted document is too large to process safely.");
            result.Append(buffer,0,count);
        }
        return result.ToString();
    }
    static void Append(JsonElement node,StringBuilder text)
    {
        if(node.ValueKind==JsonValueKind.Array){foreach(var child in node.EnumerateArray())Append(child,text);return;}
        if(node.ValueKind!=JsonValueKind.Object)return;
        var type=node.TryGetProperty("type",out var kind)?kind.GetString():null;
        if(type is "image" or "inline_image")return;
        if(type is "soft_break" or "line_break"){text.AppendLine();return;}
        if(node.TryGetProperty("text",out var value) && value.ValueKind==JsonValueKind.String)text.Append(value.GetString());
        foreach(var property in new[]{"content","children","items","table","rows","cells"})
            if(node.TryGetProperty(property,out var children))Append(children,text);
        if(type is "paragraph" or "heading" or "code_block" or "page_break")text.AppendLine();
        if(text.Length>MaxCharacters)throw new PlanningException("This brief exceeds the 60,000 character limit. Text is never silently truncated.");
    }
    public static List<Paragraph> Paragraphs(string text)=>text.Split('\n',StringSplitOptions.RemoveEmptyEntries).Select((p,i)=>new Paragraph($"P{i+1}",p.Trim())).ToList();
}
