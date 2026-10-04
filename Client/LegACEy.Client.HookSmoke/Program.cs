using System;
using System.Runtime.InteropServices;
using LegACEy.Client.DecalPlugin;
class Program {
 [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr a, UIntPtr b, uint c, uint d);
 [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Invoke(IntPtr device);
 static int Main() {
  var entry=VirtualAlloc(IntPtr.Zero,(UIntPtr)64,0x3000,0x40);
  var device=Marshal.AllocHGlobal(256);Marshal.Copy(new byte[256],0,device,256);
  var code=new byte[64];for(int i=0;i<64;i++)code[i]=0x90;
  PostUiDrawHook.Signature.CopyTo(code,0);
  code[35]=0xff;code[36]=0x06;code[37]=0x5e;code[38]=0xc3;
  Marshal.Copy(code,0,entry,64);
  var call=(Invoke)Marshal.GetDelegateForFunctionPointer(entry,typeof(Invoke));
  var draws=0;var failures=0;var fail=false;
  using(var hook=new PostUiDrawHook(()=>{draws++;if(fail)throw new Exception("draw failure");},_=>{failures++;throw new Exception("report failure");})) {
   if(!hook.InstallAt(entry)||hook.HasRun)throw new Exception("install/readiness");
   call(device);
   if(!hook.HasRun||draws!=1||Marshal.ReadInt32(device)!=1)throw new Exception("callback/forwarding");
   fail=true;call(device);
   if(hook.IsInstalled||failures!=1||Marshal.ReadInt32(device)!=2)throw new Exception("failure isolation");
   call(device);
   if(draws!=2||Marshal.ReadInt32(device)!=3)throw new Exception("disabled forwarding");
  }
  call(device);
  if(Marshal.ReadInt32(device)!=4)throw new Exception("unload forwarding");
  Console.WriteLine("PASS: native x86 callback, thiscall forwarding, draw/report failure isolation, disable and unload.");
  return 0;
 }
}