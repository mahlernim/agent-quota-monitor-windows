using AgentQuotaMonitor;
using System.Net;
using System.Text.Json;

int checks = 0;
void Assert(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
string Feed(params (string tag, bool pre, bool draft, bool asset)[] rows) => JsonSerializer.Serialize(rows.Select(r => new { tag_name = r.tag, prerelease = r.pre, draft = r.draft, html_url = "https://untrusted.example", assets = r.asset ? new[] { new { name = "app-win-x64.zip", state = "uploaded" } } : [] }));
var feed = Feed(("v1.1.0-beta.10",true,false,true),("v1.0.1",false,false,true),("v9.0.0",false,true,true),("v8.0.0",false,false,false));
Assert(UpdateService.Select(feed,"1.0.0")?.Tag == "v1.0.1", "stable ignores prerelease/draft/incomplete");
Assert(UpdateService.Select(feed,"1.1.0-beta.2")?.Tag == "v1.1.0-beta.10", "numeric prerelease order");
Assert(UpdateService.Select(Feed(("v1.1.0",false,false,true)),"1.1.0-beta.10")?.Tag == "v1.1.0", "beta to stable");
Assert(UpdateService.Select(feed,"2.0.0") is null, "no downgrade");
Assert(UpdateService.Select(Feed(("v1.0.0",false,false,true)),"1.0.0+build") is null, "equal build metadata");
Assert(UpdateService.Select(feed,"invalid") is null, "invalid installed");
Assert(SemVersion.Parse("v1.0.0-beta.01") is null, "invalid prerelease");
Assert(UpdateService.Select(feed,"1.0.0")!.Url.StartsWith(UpdateService.Repository), "fixed release origin");
var now = DateTimeOffset.Parse("2026-09-20T00:00:00Z");
var state = new UpdatePreferences(); string saved = "";
var handler = new FakeHandler(feed);
using var service = new UpdateService(new HttpClient(handler),state,p=>saved=JsonSerializer.Serialize(p),()=>now,"1.0.0");
await service.Check(); Assert(handler.Count == 1 && service.Available is not null,"initial check");
await service.Check(); Assert(handler.Count == 1,"daily limit");
service.Skip(); Assert(service.Available is null && state.Skipped=="v1.0.1","skip");
var restored = JsonSerializer.Deserialize<UpdatePreferences>(saved)!;
using var restart = new UpdateService(new HttpClient(new FakeHandler(feed)),restored,_=>{},()=>now,"1.0.0");
await restart.Check(); Assert(restart.Available is null,"restart retains daily limit");
now=now.AddDays(1); await service.Check(); Assert(service.Available is null,"skip survives automatic check");
await service.Check(true); Assert(service.Available is not null,"manual reconsider skip");
service.Later(); Assert(state.LaterUntil==now.AddDays(1)&&service.Available is null,"later snooze");
service.SetAutomatic(false); now=now.AddDays(2); int count=handler.Count;
await service.Check(); Assert(handler.Count==count,"disabled does not request");
await service.Check(true); Assert(handler.Count==count+1,"manual while disabled");
handler.Fail=true; await service.Check(true); Assert(service.Status.Contains("Could not"),"manual failure visible");
service.SetAutomatic(true); now=now.AddDays(1); await service.Check(); Assert(service.Status=="","automatic failure quiet");
count=handler.Count; await service.Check(); Assert(handler.Count==count,"failure respects cooldown");
handler.Fail=false; await service.Check(urgent:true); Assert(handler.Count==count+1,"format change skips the daily limit");
service.SetAutomatic(false); count=handler.Count;
await service.Check(urgent:true); Assert(handler.Count==count,"format change respects disabled automatic checks");
Console.WriteLine($"{checks} update checks passed");
sealed class FakeHandler(string content) : HttpMessageHandler {
 public int Count; public bool Fail;
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
  Count++; if(request.Headers.Authorization is not null) throw new Exception("Credentials sent");
  return Task.FromResult(new HttpResponseMessage(Fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent(content)});
 }
}
