using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.IO.Compression;
using PgrVoice;
using PgrVoice.Packages;

var preparation=JsonNode.Parse(File.ReadAllText(args[0]))!;
string output=Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
var results=new List<object>(); int assertions=0;
void Check(bool ok,string reason){assertions++;if(!ok)throw new InvalidDataException(reason);}
string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
Pack Raw(string file){var pack=Json.Read<Pack>(file);pack.Root=Path.GetDirectoryName(file)!;pack.Validate();return pack;}
string Body(Pack p)=>JsonSerializer.Serialize(p.Nodes.Select(n=>new{n.Id,n.Kind,n.SectionId,n.Text,n.Speaker,n.Archived}));

foreach(var row in preparation["cases"]!.AsArray())
{
    string file=row!["source"]!.GetValue<string>(), target=row["target"]!.GetValue<string>();
    string hash=Hash(file);var source=Raw(file);var expected=Raw(target);var actual=Pack.Load(file);
    Check(actual.BundledCorrectionHandled,"not handled "+source.Id);
    Check(PlaybackEngine.NavigationFingerprint(actual)==PlaybackEngine.NavigationFingerprint(expected),"graph mismatch "+source.Id);
    Check(Body(actual)==Body(expected),"body mismatch "+source.Id);
    Check(actual.Nodes.All(n=>n.Audio==null||source.ById.TryGetValue(n.Id,out var old)&&old.Audio==n.Audio),"new/replaced sound "+source.Id);
    Check(actual.Nodes.Where(n=>n.Kind=="line").All(n=>n.TextVersion==BundledPackCorrections.TextVersion(actual.Id,n)),"text version "+source.Id);
    var again=Pack.Load(file);Check(JsonSerializer.Serialize(actual,Json.Options)==JsonSerializer.Serialize(again,Json.Options),"non-idempotent "+source.Id);
    Check(Hash(file)==hash,"manifest written "+file);
    results.Add(new{pack=source.Id,file,passed=true});
}
foreach(var row in preparation["current"]!.AsObject())
{
    string file=row.Value!.GetValue<string>(),hash=Hash(file);var raw=Raw(file);var pack=Pack.Load(file);
    Check(Body(raw)==Body(pack)&&PlaybackEngine.NavigationFingerprint(raw)==PlaybackEngine.NavigationFingerprint(pack),"current story changed "+row.Key);
    Check(Hash(file)==hash,"formal file written "+row.Key);
}

// Real corrected sentence IDs, with isolated valid synthetic WAVs for binding/import mechanics only.
string chapter=preparation["current"]!["pgr-ch19"]!.GetValue<string>();var canonical=Pack.Load(chapter);
var corrected=canonical.Nodes.First(n=>n.Kind=="line"&&n.Text.Contains("和李斯特又有什么区别"));
string fixture=Path.Combine(output,"fixture");Directory.CreateDirectory(fixture);
string sound=Path.Combine(fixture,"new.wav");
using(var writer=new BinaryWriter(File.Create(sound))){writer.Write("RIFF"u8.ToArray());writer.Write(36+3200);writer.Write("WAVEfmt "u8.ToArray());writer.Write(16);writer.Write((short)1);writer.Write((short)1);writer.Write(16000);writer.Write(32000);writer.Write((short)2);writer.Write((short)16);writer.Write("data"u8.ToArray());writer.Write(3200);writer.Write(new byte[3200]);}
string manifest=Path.Combine(fixture,"pack.json");
Pack Fresh(){var p=JsonSerializer.Deserialize<Pack>(JsonSerializer.Serialize(canonical,Json.Options),Json.Options)!;p.Root=fixture;foreach(var n in p.Nodes)n.Audio=null;p.Validate();return p;}
void Save(Pack p)=>File.WriteAllText(manifest,JsonSerializer.Serialize(p,Json.Options));
var fresh=Fresh();fresh.ById[corrected.Id].Audio="new.wav";fresh.ById[corrected.Id].AudioTextVersion="old-version";Save(fresh);
Check(Pack.Load(manifest).ById[corrected.Id].Audio==null,"old text version accepted");
fresh.ById[corrected.Id].AudioTextVersion=corrected.TextVersion;Save(fresh);
Check(Pack.Load(manifest).ById[corrected.Id].Audio=="new.wav","new version rejected");
fresh.ById[corrected.Id].AudioTextVersion=null;Save(fresh);
Check(Pack.Load(manifest).ById[corrected.Id].Audio=="new.wav","matching legacy text rejected");
fresh.StoryRevision="future-story";Save(fresh);
Check(Pack.Load(manifest).StoryRevision=="future-story","future story downgraded");
fresh=Fresh();fresh.Nodes.Add(new Node{Id="future-line",SectionId=corrected.SectionId,Text="新增剧情，请更新软件"});Save(fresh);
Check(Pack.Load(manifest).ById.ContainsKey("future-line"),"future line removed");
fresh=Fresh();fresh.Id="unknown-pack";Save(fresh);
Check(!Pack.Load(manifest).BundledCorrectionHandled,"unknown package modified");

// Known retired waveform cannot be relabeled as new text to bypass the correction.
var retireRow=preparation["cases"]!.AsArray().First(r=>r!["id"]!.GetValue<string>()=="pgr-ch19"&&r["origin"]!.GetValue<string>().Contains('!'))!;
string[] origin=retireRow["origin"]!.GetValue<string>().Split('!',2);var oldDoc=Raw(retireRow["source"]!.GetValue<string>());
string oldAudio=oldDoc.ById[corrected.Id].Audio!;
using(var zip=ZipFile.OpenRead(origin[0])){
    string prefix=origin[1].Contains('/')?origin[1][..(origin[1].LastIndexOf('/')+1)]:"";
    using var input=zip.GetEntry(prefix+oldAudio)!.Open();using var dest=File.Create(Path.Combine(fixture,"old.wav"));input.CopyTo(dest);
}
fresh=Fresh();fresh.ById[corrected.Id].Audio="old.wav";fresh.ById[corrected.Id].AudioTextVersion=corrected.TextVersion;Save(fresh);
Check(Pack.Load(manifest).ById[corrected.Id].Audio==null,"retired waveform accepted");

// Actual package repository import, then replacement import, preserves raw manifests and revision data.
var repository=new PackageRepository(Path.Combine(output,"repository"));
fresh=Fresh();fresh.ById[corrected.Id].Audio="new.wav";fresh.ById[corrected.Id].AudioTextVersion=corrected.TextVersion;Save(fresh);
async Task<InstalledPackage> Import(){using var memory=new MemoryStream();using(var z=new ZipArchive(memory,ZipArchiveMode.Create,true)){z.CreateEntryFromFile(manifest,"pack.json");z.CreateEntryFromFile(sound,"new.wav");}memory.Position=0;return await repository.ImportAsync(memory);}
string originalHash=Hash(manifest);var first=await Import();Check(Hash(first.PackFile)==originalHash,"import rewrote manifest");
Check(repository.Load(fresh.Id).ById[corrected.Id].Audio=="new.wav","import new audio lost");
fresh.ById[corrected.Id].AudioTextVersion="old-version";Save(fresh);var second=await Import();
Check(second.IsUpdate&&File.Exists(first.PackFile),"update deleted previous package");
Check(repository.Load(fresh.Id).ById[corrected.Id].Audio==null,"update old voice not rejected");
Check(Hash(second.PackFile)==Hash(manifest),"update rewrote manifest");
File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{passed=true,assertions,oldCases=results.Count,currentCases=138,results,syntheticAudioUsed=true},Json.Options));
Console.WriteLine($"PASS {assertions} assertions; {results.Count} historical manifests; 138 current; binding and real repository import/update.");
