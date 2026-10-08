using System;
using System.Drawing;
using System.Runtime.InteropServices;
using LegACEy.Client.DecalPlugin;
class Program {
 [DllImport("kernel32.dll")] static extern IntPtr VirtualAlloc(IntPtr a, UIntPtr b, uint c, uint d);
 [DllImport("kernel32.dll")] static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);
 [UnmanagedFunctionPointer(CallingConvention.ThisCall)] delegate void Invoke(IntPtr device);
 static int Main() {
  var entry=VirtualAlloc(IntPtr.Zero,(UIntPtr)64,0x3000,0x40);
  var device=Marshal.AllocHGlobal(256);Marshal.Copy(new byte[256],0,device,256);
  var code=new byte[64];for(int i=0;i<64;i++)code[i]=0x90;
  PostUiDrawHook.Signature.CopyTo(code,0);
  code[35]=0xff;code[36]=0x06;code[37]=0x5e;code[38]=0xc3;
  Marshal.Copy(code,0,entry,64);
  var call=(Invoke)Marshal.GetDelegateForFunctionPointer(entry,typeof(Invoke));
  var draws=0;var failures=0;var fail=false;var removedLogs=0;
  using(var hook=new PostUiDrawHook(()=>{draws++;if(fail)throw new Exception("draw failure");},_=>{failures++;throw new Exception("report failure");},message=>{if(message=="Retail EndScene hook removed.")removedLogs++;})) {
   if(!hook.InstallAt(entry)||hook.HasRun)throw new Exception("install/readiness");
   call(device);
   if(!hook.HasRun||draws!=1||Marshal.ReadInt32(device)!=1)throw new Exception("callback/forwarding");
   fail=true;call(device);
   if(hook.IsInstalled||failures!=1||Marshal.ReadInt32(device)!=2)throw new Exception("failure isolation");
   call(device);
   if(draws!=2||Marshal.ReadInt32(device)!=3)throw new Exception("disabled forwarding");
  }
  if(removedLogs!=1)throw new Exception("removal logged once");
  call(device);
  if(Marshal.ReadInt32(device)!=4)throw new Exception("unload forwarding");
  Console.WriteLine("PASS: native x86 callback, thiscall forwarding, draw/report failure isolation, disable and unload.");
  CheckMovementOverride();
  return 0;
 }
    static void CheckMovementOverride()
    {
        // Geometry and saved character settings are distinct fields, as in retail.
        // The fixture override uses ECX as receiver and removes its two stack arguments.
        var overrideEntry = VirtualAlloc(IntPtr.Zero, (UIntPtr)64, 0x3000, 0x40);
        if (overrideEntry == IntPtr.Zero) throw new Exception("movement fixture allocation");
        var persist = new byte[] {
            0x8b, 0x44, 0x24, 0x04, // mov eax, [esp+4] (X)
            0x89, 0x41, 0x04,       // mov [ecx+4], eax (geometry X)
            0x89, 0x41, 0x0c,       // mov [ecx+12], eax (saved X)
            0x8b, 0x44, 0x24, 0x08, // mov eax, [esp+8] (Y)
            0x89, 0x41, 0x08,       // mov [ecx+8], eax (geometry Y)
            0x89, 0x41, 0x10,       // mov [ecx+16], eax (saved Y)
            0xc2, 0x08, 0x00        // ret 8
        };
        Marshal.Copy(persist, 0, overrideEntry, persist.Length);
        var vtable = Marshal.AllocHGlobal(0x30);
        var element = Marshal.AllocHGlobal(20);
        try
        {
            Marshal.Copy(new byte[0x30], 0, vtable, 0x30);
            Marshal.Copy(new byte[20], 0, element, 20);
            Marshal.WriteIntPtr(vtable, 0x2c, overrideEntry);
            Marshal.WriteIntPtr(element, vtable);
            if (NativeUiMovement.ResolveMoveTo(element) != overrideEntry)
                throw new Exception("FAIL: native movement resolves the base function instead of the element override that saves character position.");
            NativeUiMovement.MoveTo(element, new Point(544, 539));
            if (Marshal.ReadInt32(element, 4) != 544 || Marshal.ReadInt32(element, 8) != 539)
                throw new Exception("native movement geometry");
            if (Marshal.ReadInt32(element, 12) != 544 || Marshal.ReadInt32(element, 16) != 539)
                throw new Exception("FAIL: native movement bypassed the element override and left saved character position at origin.");
            NativeUiMovement.MoveTo(element, Point.Empty);
            if (Marshal.ReadInt32(element, 12) != 0 || Marshal.ReadInt32(element, 16) != 0)
                throw new Exception("native override origin writeback");
            Console.WriteLine("PASS: native x86 movement dispatch reaches the element override and its saved-position writeback.");
        }
        finally
        {
            Marshal.FreeHGlobal(element);
            Marshal.FreeHGlobal(vtable);
            VirtualFree(overrideEntry, UIntPtr.Zero, 0x8000);
        }
    }
}
