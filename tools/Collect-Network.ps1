param([string]$LauncherRoot)
$ErrorActionPreference='Stop'
if(-not $LauncherRoot){$LauncherRoot=Split-Path -Parent $PSCommandPath}
$LauncherRoot=[IO.Path]::GetFullPath($LauncherRoot)
$out=Join-Path $LauncherRoot ('out\network-diagnostic-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
[IO.Directory]::CreateDirectory((Split-Path -Parent $out))|Out-Null
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
public static class GkmTlsProbe {
 public static Dictionary<string,object> Check(string host,string proxy) {
  var r=new Dictionary<string,object>();r["host"]=host;r["route"]=string.IsNullOrWhiteSpace(proxy)?"direct":"configured-proxy";r["tlsValidated"]=false;
  try { using(var client=new TcpClient()) {
   Uri p=string.IsNullOrWhiteSpace(proxy)?null:new Uri(proxy.Contains("://")?proxy:"http://"+proxy);
   var connect=client.ConnectAsync(p==null?host:p.Host,p==null?443:p.Port);
   if(!connect.Wait(5000))throw new TimeoutException("TCP connection timed out");connect.GetAwaiter().GetResult();
   using(var wire=client.GetStream()) {
    wire.ReadTimeout=5000;wire.WriteTimeout=5000;
    if(p!=null) {if(p.Scheme!="http")throw new Exception("Only HTTP CONNECT proxy is probed");
     byte[] b=Encoding.ASCII.GetBytes("CONNECT "+host+":443 HTTP/1.1\r\nHost: "+host+":443\r\n\r\n");wire.Write(b,0,b.Length);
     var text=new StringBuilder();while(text.Length<8192){int c=wire.ReadByte();if(c<0)throw new Exception("Proxy closed CONNECT");text.Append((char)c);if(text.ToString().EndsWith("\r\n\r\n"))break;}
     string first=text.ToString().Split('\n')[0].Trim();r["connectStatus"]=first;
     if(!first.StartsWith("HTTP/1.1 200")&&!first.StartsWith("HTTP/1.0 200"))throw new Exception("CONNECT rejected");
    }
    using(var tls=new SslStream(wire,false,(sender,cert,chain,errors)=>{
     r["policyErrors"]=errors.ToString();if(cert!=null){var x=new X509Certificate2(cert);r["subject"]=x.Subject;r["issuer"]=x.Issuer;r["thumbprint"]=x.Thumbprint;r["notBefore"]=x.NotBefore.ToUniversalTime().ToString("o");r["notAfter"]=x.NotAfter.ToUniversalTime().ToString("o");}
     var statuses=new List<string>();if(chain!=null)foreach(var s in chain.ChainStatus)statuses.Add(s.Status.ToString());r["chainStatus"]=statuses.ToArray();
     return errors==SslPolicyErrors.None; // Never disables certificate validation.
    })) {tls.ReadTimeout=5000;tls.WriteTimeout=5000;tls.AuthenticateAsClient(host,null,SslProtocols.Tls12,false);r["tlsValidated"]=true;}
   }
  }}catch(Exception e){r["error"]=e.GetBaseException().Message;}
  return r;
 }
}
'@
$settings=Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
function SafeUri([string]$value){try{$u=[Uri]($value -replace '^(?!\w+://)','http://');return $u.Scheme+'://'+$u.Host+':'+$u.Port}catch{return '[unparsed]'}}
$proxyText=[string]$settings.ProxyServer
$endpoint=$null
if([int]$settings.ProxyEnable -eq 1){
    if($proxyText.Contains('=')){$item=@($proxyText -split ';' | Where-Object {$_ -match '^https='}) | Select-Object -First 1;if($item){$endpoint=($item -split '=',2)[1]}}
    else{$endpoint=$proxyText}
}
$report=[ordered]@{schema=1;utc=[DateTime]::UtcNow.ToString('o');proxyEnabled=[int]$settings.ProxyEnable;proxy=if($endpoint){SafeUri $endpoint}else{$null};pacConfigured=[bool]$settings.AutoConfigURL;probes=@();certificates=@();listeners=@()}
foreach($store in @('Cert:\CurrentUser\Root','Cert:\LocalMachine\Root')){
    $report.certificates+=@(Get-ChildItem $store -ErrorAction SilentlyContinue | Where-Object {$_.Subject -like '*GKM*'} | ForEach-Object {@{store=$store;subject=$_.Subject;thumbprint=$_.Thumbprint;expires=$_.NotAfter.ToUniversalTime().ToString('o')}})
}
$report.listeners=@(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object {$_.LocalAddress -in @('127.0.0.1','0.0.0.0','::1','::')} | ForEach-Object {$p=Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue;if($_.LocalPort -in @(7899,18443,18444,18446,18502) -or $p.ProcessName -match 'clash|mihomo'){@{port=$_.LocalPort;pid=$_.OwningProcess;process=$p.ProcessName}}})
foreach($hostName in @('api.store.games.dmm.com','identitytoolkit.googleapis.com','api.game-gakuen-idolmaster.jp')){
    $report.probes+=,[GkmTlsProbe]::Check($hostName,$null)
    if($endpoint){$report.probes+=,[GkmTlsProbe]::Check($hostName,$endpoint)}
}
$report | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath $out -Encoding UTF8
Write-Host ('Report saved: '+$out)
Write-Host 'No accounts, launch arguments, browser data or HTTP bodies collected. No proxy, certificate or game settings changed.'
