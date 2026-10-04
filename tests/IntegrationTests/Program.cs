using PgrVoice;
using System.Diagnostics;
using System.Text.Json;

string root=Path.GetFullPath(args[0]);
Log.DataDir=Path.Combine(root,"reports","integration-state");
var pack=Pack.Load(Path.Combine(root,"packs","第29章试用包","pack.json"));
var wav=pack.ResolveAudio(pack.Nodes.First(n=>n.Audio!=null))!;
using(var audio=new AudioService())
{
    audio.Volume=0;audio.Play(wav);await Task.Delay(120);
    if(!audio.Playing)throw new Exception("WAV playback not started");
    audio.Volume=0;audio.Stop();if(audio.Playing)throw new Exception("stop failed");
    Console.WriteLine("PASS WAV opens, starts and stops through real output device (muted)");
    var mp3=Path.Combine(root,"reports","integration-silent.mp3");
    if(File.Exists(mp3)) { audio.Play(mp3);await Task.Delay(100);if(!audio.Playing)throw new Exception("MP3 failed");audio.Stop();Console.WriteLine("PASS MP3 real output device (muted)"); }
}
using(var missing=new OcrService(Path.Combine(root,"missing.exe")))
{
    try {await missing.Recognize("irrelevant",null,"original",CancellationToken.None);throw new Exception("missing worker accepted");}catch(FileNotFoundException){}
    if(missing.Running)throw new Exception("missing worker stays alive");
    Console.WriteLine("PASS missing OCR component fails without starting a process");
}
using(var service=new OcrService(Path.Combine(root,"dist","ocr-build","PgrOcr","PgrOcr.exe")))
{
    string image=Path.Combine(root,"reports","ocr-samples","frame-00030.0.png");
    var sw=Stopwatch.StartNew();
    var cold=await service.Recognize(image,null,"original",CancellationToken.None);double coldMs=sw.Elapsed.TotalMilliseconds;
    if(!cold.Blocks.Any(b=>b.Text.Contains("文明")))throw new Exception("OCR text missing");
    sw.Restart();var warm=await service.Recognize(image,null,"original",CancellationToken.None);double warmMs=sw.Elapsed.TotalMilliseconds;
    Console.WriteLine($"PASS packaged OCR protocol: cold {coldMs:F0}ms, warm {warmMs:F0}ms");
    using(var cts=new CancellationTokenSource())
    {
        cts.CancelAfter(1);
        try{await service.Recognize(image,null,"contrast",cts.Token);throw new Exception("cancel did not throw");}catch(OperationCanceledException){}
    }
    // 取消可发生在进入进程前或等待结果中；显式关闭始终释放资源。
    service.Stop();if(service.Running)throw new Exception("OCR still alive");
    var restarted=await service.Recognize(image,null,"original",CancellationToken.None);
    if(!restarted.Blocks.Any(b=>b.Text.Contains("文明")))throw new Exception("OCR restart failed");
    Console.WriteLine("PASS cancellation, shutdown, restart and request isolation");
    var memory=Process.GetProcessesByName("PgrOcr").Where(p=>p.Id!=Environment.ProcessId).Select(p=>new {p.Id,workingSetMb=p.WorkingSet64/1048576.0,privateMb=p.PrivateMemorySize64/1048576.0}).ToList();
    Json.Save(Path.Combine(root,"reports","integration-performance.json"),new {coldMs,warmMs,workerProcesses=memory,machine=new {os=Environment.OSVersion.ToString(),logicalProcessors=Environment.ProcessorCount}});
    Console.WriteLine("WAIT idle worker eviction (120s policy)");
    await Task.Delay(132000);
    if(service.Running)throw new Exception("OCR idle process still alive after 132s");
    Console.WriteLine("PASS idle OCR process exits automatically");
}
