namespace Samicpp.Web;

using Samicpp.Http;

using System;
using System.Threading.Tasks;
using Microsoft.DotNet.Interactive;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Commands;
using System.Collections.Generic;
using System.Threading;
using Microsoft.DotNet.Interactive.FSharp;

public enum ScriptType
{
    CSharp,
    FSharp,
}

public class ScriptNode
{
    // readonly CompositeKernel kernel = new CompositeKernel().UseValueSharing().UseWho();
    // readonly CSharpKernel cskernel = new CSharpKernel().UseValueSharing().UseWho().UseKernelHelpers();
    readonly Dictionary<ScriptType, Kernel> kernels = [];
    SemaphoreSlim klock = new(1, 1);
    bool locked;
    public bool Locked => locked;

    public ScriptNode()
    {
        CSharpKernel cskernel = new CSharpKernel().UseValueSharing().UseWho().UseKernelHelpers();
        FSharpKernel fskernel = new FSharpKernel().UseValueSharing().UseWho().UseKernelHelpers();
        kernels[ScriptType.CSharp] = cskernel;
        kernels[ScriptType.FSharp] = fskernel;
    }

    public async Task<KernelCommandResult> Execute(string code, IDualHttpSocket socket, ScriptType type)
    {
        await klock.WaitAsync();
        locked = true;
        try
        {
            // await cskernel.SendAsync(new SubmitCode($"var socket = ({typeof(IDualHttpSocket).FullName})KernelGlobals.socket;\n"));

            // KernelGlobals
            if (type == ScriptType.CSharp && kernels[ScriptType.CSharp] is CSharpKernel cskernel)
            {
                // await cskernel.SetValueAsync("socket", socket, typeof(IDualHttpSocket));

                var result = await cskernel.SendAsync(new SubmitCode(code));

                // foreach (var ev in result.Events)
                // {
                //     switch (ev)
                //     {
                //         case CommandFailed failed:
                //             Console.WriteLine("script failed " + failed.Message);
                //             break;
                //         case CommandSucceeded:
                //             break;
                //         case DisplayedValueProduced display:
                //             Console.WriteLine("script output " + display.Value);
                //             break;
                //     }
                // }

                return result;
            }
            else if (type == ScriptType.FSharp && kernels[ScriptType.FSharp] is FSharpKernel kernel)
            {
                // await kernel.SetValue
                // var result = await kernel.SendAsync(new SubmitCode(code));
                // return result;
            }

            return null;
        }
        finally
        {
            klock.Release();
            locked = false;
        }
    }
}
