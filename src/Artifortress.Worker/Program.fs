open System
open System.Threading

[<EntryPoint>]
let main _ =
    use stopToken = new CancellationTokenSource()

    Console.CancelKeyPress.Add(fun args ->
        args.Cancel <- true
        stopToken.Cancel()
    )

    printfn "Artifortress worker started. Press Ctrl+C to stop."

    while not stopToken.IsCancellationRequested do
        printfn "worker_heartbeat_utc=%O" DateTimeOffset.UtcNow
        Thread.Sleep(TimeSpan.FromSeconds(30.0))

    printfn "Artifortress worker stopped."
    0
