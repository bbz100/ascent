using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Ascent.Web.Planning;
namespace Ascent.Web.Infrastructure;

public sealed class UploadArchiveOptions
{
    public string Path {get;set;}="App_Data/Uploads";
}
public sealed class DocumentArchive(IOptions<UploadArchiveOptions> options)
{
    public async Task<ArchivedDocument> StoreAsync(string name,Stream source,CancellationToken ct=default)
    {
        var extension=System.IO.Path.GetExtension(name).ToLowerInvariant();
        if(!DocumentReader.Supported(name))throw new PlanningException("This document format is not supported.");
        var bytes=new MemoryStream();
        try
        {
            var buffer=new byte[81920];int count;
            while((count=await source.ReadAsync(buffer,ct))>0)
            {
                if(bytes.Length+count>DocumentReader.MaxBytes)throw new PlanningException("Upload a file smaller than 10 MB.");
                await bytes.WriteAsync(buffer.AsMemory(0,count),ct);
            }
            var root=System.IO.Path.GetFullPath(options.Value.Path);
            Directory.CreateDirectory(root);
            var hash=Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0,(int)bytes.Length))).ToLowerInvariant();
            var storedFile=hash+extension;
            var destination=System.IO.Path.Combine(root,storedFile);
            var temp=System.IO.Path.Combine(root,Guid.NewGuid().ToString("N")+".tmp");
            try
            {
                await File.WriteAllBytesAsync(temp,bytes.ToArray(),ct);
                try {File.Move(temp,destination);}
                catch(IOException) when(File.Exists(destination)) { /* Identical file already archived. */ }
            }
            finally {if(File.Exists(temp))File.Delete(temp);}
            var record=new {OriginalFileName=System.IO.Path.GetFileName(name),StoredFile=storedFile,Sha256=hash,Size=bytes.Length,UploadedUtc=DateTime.UtcNow};
            await File.WriteAllTextAsync(System.IO.Path.Combine(root,$"{hash}.{Guid.NewGuid():N}.json"),JsonSerializer.Serialize(record,new JsonSerializerOptions{WriteIndented=true}),ct);
            bytes.Position=0;
            return new(storedFile,System.IO.Path.GetFileName(name),bytes);
        }
        catch {await bytes.DisposeAsync();throw;}
    }
    public string? FindPath(string storedFile)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(storedFile,@"\A[a-f0-9]{64}\.[a-z0-9]+\z") || !DocumentReader.Supported(storedFile))return null;
        var path=System.IO.Path.Combine(System.IO.Path.GetFullPath(options.Value.Path),storedFile);
        return File.Exists(path)?path:null;
    }
}
public sealed record ArchivedDocument(string StoredFile,string OriginalFileName,MemoryStream Content):IAsyncDisposable
{public ValueTask DisposeAsync()=>Content.DisposeAsync();}
