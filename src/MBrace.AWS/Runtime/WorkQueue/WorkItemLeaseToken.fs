﻿namespace MBrace.AWS.Runtime

open System
open System.Collections.Generic
open System.IO
open System.Threading
open System.Runtime.Serialization

open Microsoft.FSharp.Control

open MBrace.Core
open MBrace.Core.Internals
open MBrace.Runtime
open MBrace.Runtime.Utils

open Amazon.SQS.Model
open FSharp.AWS.DynamoDB

open MBrace.AWS.Runtime
open MBrace.AWS.Runtime.Utilities

type internal WorkItemQueueSettings =
    static member VisibilityTimeout = 30000
    static member RenewInterval = 10000

type internal WorkItemMessage =
    {
        Version       : Version
        ProcessId     : string
        WorkItemId    : Guid
        BatchIndex    : int option
        TargetWorker  : string option
        BlobUri       : string
        CancellationToken : string option
    }

    override this.ToString() = sprintf "workItem:%O" this.WorkItemId

    member __.TableKey = 
        TableKey.Combined(WorkItemRecord.GetHashKey  __.ProcessId, 
                            WorkItemRecord.GetRangeKey __.WorkItemId)

    member m.ToSqsMessageBody() = toJson m

    static member FromDequeuedSqsMessage(message : SqsDequeueMessage) =
        fromJson<WorkItemMessage> message.Message.Body

    member __.GetCancellationToken(clusterId) = 
        DynamoDBCancellationToken.FromUUID(clusterId, __.CancellationToken)

type internal LeaseAction =
    | Complete
    | Abandon

/// Periodically renews lock for supplied work item, releases lock if specified as completed.
[<Sealed; AutoSerializable(false)>]
type internal WorkItemLeaseMonitor private (message : SqsDequeueMessage, info : WorkItemMessage, logger : ISystemLogger) =
    let rec renewLoop (inbox : MailboxProcessor<LeaseAction>) = async {
        let! action = inbox.TryReceive(timeout = 60)
        match action with
        | None ->
            do! Async.Sleep WorkItemQueueSettings.RenewInterval

            // hide message from other workers for another 30 seconds
            let! res = message.RenewLock(timeoutMilliseconds = WorkItemQueueSettings.VisibilityTimeout) |> Async.Catch

            match res with
            | Choice1Of2 _ -> 
                logger.Logf LogLevel.Debug "%A : lock renewed" info
                return! renewLoop inbox

            | Choice2Of2 (:? ReceiptHandleIsInvalidException) ->
                logger.Logf LogLevel.Warning "%A : lock lost" info

            | Choice2Of2 exn -> 
                logger.LogError <| sprintf "%A : lock renew failed with %A" info exn
                return! renewLoop inbox

        | Some Complete ->
            do! message.Complete()
            logger.LogInfof "%A : completed" info

        | Some Abandon ->
            do! message.Abandon()
            logger.LogInfof "%A : abandoned" info
    }

    let cts = new CancellationTokenSource()
    let mbox = MailboxProcessor.Start(renewLoop, cts.Token)

    member __.CompleteWith(action) = mbox.Post action

    interface IDisposable with 
        member __.Dispose() = cts.Cancel()

    static member Start(message : SqsDequeueMessage, info : WorkItemMessage, logger : ISystemLogger) =
        new WorkItemLeaseMonitor(message, info, logger)

/// Implements ICloudWorkItemLeaseToken
type internal WorkItemLeaseToken =
    {
        ClusterId       : ClusterId
        CompleteAction  : MarshaledAction<LeaseAction> // ensures that LeaseMonitor is serializable across AppDomains
        WorkItemType    : CloudWorkItemType
        WorkItemSize    : int64
        TypeName        : string
        FaultInfo       : CloudWorkItemFaultInfo
        MessageInfo     : WorkItemMessage
        ProcessInfo     : CloudProcessInfo
    }

    member private __.Table = __.ClusterId.GetRuntimeTable<WorkItemRecord>()
    
    interface ICloudWorkItemLeaseToken with
        member this.DeclareCompleted() : Async<unit> = async {
            this.CompleteAction.Invoke Complete
            this.CompleteAction.Dispose() // disconnect marshaled object

            let! _ = this.Table.UpdateItemAsync(this.MessageInfo.TableKey, setWorkItemCompleted DateTimeOffset.Now)
            return ()
        }
        
        member this.DeclareFaulted(edi : ExceptionDispatchInfo) : Async<unit> = async {
            this.CompleteAction.Invoke Abandon
            this.CompleteAction.Dispose() // disconnect marshaled object

            let! _ = this.Table.UpdateItemAsync(this.MessageInfo.TableKey, setWorkItemFaulted (Some edi) DateTimeOffset.Now)
            return ()
        }
        
        member this.FaultInfo : CloudWorkItemFaultInfo = this.FaultInfo
        
        member this.GetWorkItem() : Async<CloudWorkItem> = async { 
            let! payload = S3Persist.ReadPersistedClosure<MessagePayload>(this.ClusterId, this.MessageInfo.BlobUri)
            match payload with
            | Single item -> return item
            | Batch items -> return items.[Option.get this.MessageInfo.BatchIndex]
        }
        
        member this.Id : CloudWorkItemId = this.MessageInfo.WorkItemId
        
        member this.WorkItemType : CloudWorkItemType = this.WorkItemType
        
        member this.Size : int64 = this.WorkItemSize
        
        member this.TargetWorker : IWorkerId option = 
            match this.MessageInfo.TargetWorker with
            | None   -> None
            | Some w -> Some(WorkerId(w) :> _)
        
        member this.Process : ICloudProcessEntry = 
            new CloudProcessEntry(this.ClusterId, this.MessageInfo.ProcessId, this.ProcessInfo) :> _
        
        member this.Type : string = this.TypeName

    /// Creates a new WorkItemLeaseToken with supplied configuration parameters
    static member Create
            (clusterId : ClusterId, 
             info      : WorkItemMessage, 
             monitor   : WorkItemLeaseMonitor, 
             faultInfo : CloudWorkItemFaultInfo) = async {

        let! processRecordT = 
            CloudProcessRecord.GetProcessRecord(clusterId, info.ProcessId) 
            |> Async.StartChild

        let! workRecord = 
            clusterId.GetRuntimeTable<WorkItemRecord>()
                     .GetItemAsync(info.TableKey)

        let! processRecord = processRecordT

        return {
                    ClusterId      = clusterId
                    CompleteAction = MarshaledAction.Create monitor.CompleteWith
                    WorkItemSize   = workRecord.Size
                    WorkItemType   = workRecord.Type
                    TypeName       = workRecord.TypeName
                    FaultInfo      = faultInfo
                    MessageInfo    = info
                    ProcessInfo    = processRecord.ToCloudProcessInfo clusterId
               }
    }