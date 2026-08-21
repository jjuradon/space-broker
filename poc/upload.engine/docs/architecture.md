# Architecture — C4 Diagrams

Three levels, each zooming into the previous diagram's most relevant box.
See also [docs/state-diagram.md](state-diagram.md) for `UploadSession`'s
own state machine, which these structural diagrams intentionally don't
attempt to show.

## Level 1 — System Context

Unchanged by the rename — FlowX.Upload is invisible at this level, an
internal building block of the Inventory Service, not a separate system.

```mermaid
C4Context
    title System Context — FlowX.Upload in the Inventory Service

    Person(manager, "Branch Manager", "Uploads Excel files to bulk update a branch's product offerings")

    System(inventoryService, "Inventory Service", "ASP.NET Core service that uses FlowX.Upload to run the bulk-upload pipeline")

    System_Ext(providerApi, "Provider Catalog API", "External system owning the master product catalog")
    SystemDb_Ext(sharedDb, "Shared Database", "Relational database shared across services in the application")

    Rel(manager, inventoryService, "Uploads file, reviews plan, confirms changes", "HTTPS/JSON")
    Rel(inventoryService, providerApi, "Looks up / creates provider products", "HTTPS/REST")
    Rel(inventoryService, sharedDb, "Reads & writes domain data and upload session state", "EF Core / SQL")
```

## Level 2 — Containers

Terminology updated: the background worker's enqueue/run methods are now
named for the Plan stage, not PreProcess.

```mermaid
C4Container
    title Container Diagram — Inventory Service

    Person(manager, "Branch Manager")

    System_Boundary(inventoryService, "Inventory Service") {
        Container(api, "Inventory API", "ASP.NET Core Web API", "Exposes upload start/status/corrections/confirm/retry endpoints")
        Container(orchestrator, "UploadOrchestrator", "FlowX.Upload (in-process)", "Coordinates the session lifecycle")
        Container(worker, "BackgroundServiceUploadExecutor", ".NET BackgroundService", "Runs Plan (Prepare+Parse+Plan) and Process work items — single pod")
        Container(reconciler, "StaleSessionReconciler", ".NET IHostedService", "Recovers or fails stale sessions on startup")
        Container(pipeline, "Inventory Upload Pipeline", "Application/Infrastructure layer", "Preparer, Parser, Planner, Processor, Corrector for this use case")
    }

    ContainerDb(uploadStore, "Upload Session Store", "flowx_upload schema, shared DB", "Durable UploadSession + Plan state")
    ContainerDb(domainDb, "Domain Tables", "Shared DB", "Products, BranchOfferings, ProductDetails")
    System_Ext(providerApi, "Provider Catalog API")

    Rel(manager, api, "HTTP", "REST/JSON, multipart upload")
    Rel(api, orchestrator, "Start / Confirm / SubmitCorrections / Retry", "in-process call")
    Rel(orchestrator, uploadStore, "Save / Get session", "EF Core")
    Rel(orchestrator, worker, "EnqueuePlanningAsync / EnqueueProcessingAsync", "in-memory channel")
    Rel(worker, orchestrator, "RunPlanningAsync / RunRevalidationAsync / RunProcessingAsync", "in-process call")
    Rel(worker, pipeline, "Prepare / Parse / Plan / Process", "in-process call")
    Rel(pipeline, domainDb, "Commit confirmed plan, per chunk, transactional", "EF Core")
    Rel(pipeline, providerApi, "Pre-action: create provider product", "HTTPS")
    Rel(reconciler, uploadStore, "Scan for stale sessions on startup", "EF Core")
    Rel(reconciler, worker, "Re-enqueue recoverable sessions", "in-process call")
```

## Level 3 — Components (inside FlowX.Upload)

`IUploadPreProcessor` renamed to `IUploadPlanner`; `PreProcessResult`
renamed to `PlanResult` (not shown as a component here — it's a return
type, documented in the usage guide). Folder namespace boundaries now
reflect the Clean Architecture layout (`Application.Abstractions.*`,
`Application.Pipelines`, `Application.UseCases`).

```mermaid
C4Component
    title Component Diagram — FlowX.Upload Core Library

    Container_Boundary(flowxUpload, "FlowX.Upload (core package)") {
        Component(uploadSession, "UploadSession", "Domain.Aggregates", "Guards all lifecycle transitions")
        Component(orchestrator, "UploadOrchestrator", "Application.UseCases", "Sole place orchestration logic lives")
        Component(registry, "IUploadPipelineRegistry", "Application.Pipelines", "Resolves a pipeline by string key")
        Component(pipelineFacade, "IUploadPipeline", "Application.Pipelines", "Type-erased view over one TContext/TParsed pipeline")
        Component(storePort, "IUploadStore", "Application.Abstractions.Persistence", "Persistence abstraction, no assumed technology")
        Component(executorPort, "IUploadExecutor", "Application.Abstractions.Scheduling", "Scheduling abstraction")
        Component(bgExecutor, "BackgroundServiceUploadExecutor", "Infrastructure.Scheduling", "Default single-pod in-memory executor")
        Component(reconciler, "StaleSessionReconciler", "Infrastructure.Scheduling", "Startup recovery sweep")
    }

    Container_Boundary(consumingApp, "Consuming Application") {
        Component(preparer, "IUploadContextPreparer<T>", "App-defined", "Stage 1 (Prepare): file -> durable context")
        Component(parser, "IUploadParser<TContext,TParsed>", "App-defined", "Stage 2 (Parse): file + context -> parsed shape")
        Component(planner, "IUploadPlanner<TParsed>", "App-defined, RENAMED from IUploadPreProcessor", "Stage 3 (Plan): validate + build plan")
        Component(corrector, "IUploadCorrector<TContext,TParsed>", "App-defined, optional", "Applies UI corrections")
        Component(processor, "IUploadProcessor", "App-defined", "Stage 4 (Process): execute confirmed plan")
        Component(efStore, "EfUploadStore", "FlowX.Upload.EntityFrameworkCore", "Reference IUploadStore implementation")
    }

    Rel(orchestrator, uploadSession, "Invokes lifecycle methods on")
    Rel(orchestrator, registry, "Resolves pipeline via")
    Rel(orchestrator, storePort, "Persists / loads session via")
    Rel(orchestrator, executorPort, "Enqueues work via")
    Rel(registry, pipelineFacade, "Returns")
    Rel(pipelineFacade, preparer, "Delegates PrepareAsync to")
    Rel(pipelineFacade, parser, "Delegates ParseAsync to")
    Rel(pipelineFacade, planner, "Delegates PlanAsync to")
    Rel(pipelineFacade, processor, "Delegates ProcessAsync to")
    Rel(pipelineFacade, corrector, "Delegates ApplyCorrections to, if registered")
    Rel(executorPort, bgExecutor, "Default implementation")
    Rel(storePort, efStore, "One possible implementation")
    Rel(reconciler, storePort, "Scans via")
    Rel(reconciler, executorPort, "Re-enqueues via")
```
