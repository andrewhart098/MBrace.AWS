﻿namespace MBrace.AWS.Service

open System
open System.Threading
open System.Diagnostics

open MBrace.Runtime
open MBrace.Runtime.Utils
open MBrace.AWS
open MBrace.AWS.Runtime
open MBrace.AWS.Runtime.Arguments

module StandaloneWorker =

    /// Recommended main method for running a standalone AWS worker 
    /// with provided command line arguments
    let main (args : string []) : int =
        try
            let cli = ArgumentConfiguration.FromCommandLineArguments args
            let config = Option.get cli.Configuration // successful parsing always yields 'Some' here
            let proc = Process.GetCurrentProcess()
            let workerId = 
                match cli.WorkerId with
                | None -> sprintf "%s-p%d" (System.Net.Dns.GetHostName()) proc.Id
                | Some n -> n

            let svc = new WorkerService(config, workerId)
            if not cli.Quiet then
                ignore <| svc.AttachLogger(ConsoleLogger(showDate = true, useColors = true))

            cli.WorkingDirectory |> Option.iter (fun w -> svc.WorkingDirectory <- w)
            cli.MaxWorkItems |> Option.iter (fun w -> svc.MaxConcurrentWorkItems <- w)
            cli.HeartbeatInterval |> Option.iter (fun i -> svc.HeartbeatInterval <- i)
            cli.HeartbeatThreshold |> Option.iter (fun i -> svc.HeartbeatThreshold <- i)
            svc.LogFile <- defaultArg cli.LogFile "logs.txt"
            cli.LogLevel |> Option.iter (fun l -> svc.LogLevel <- l)

            Console.Title <- sprintf "%s(%d) : %s"  proc.ProcessName proc.Id svc.Id
            svc.Run()
            0

        with e ->
            printfn "Unhandled exception : %O" e
            Thread.Diverge()