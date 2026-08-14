# Architecture — C4 Diagrams

Three levels, each zooming into the previous diagram's most relevant box.
If your Mermaid renderer doesn't support the `C4Context`/`C4Container`/
`C4Component` diagram types (they're relatively recent), render these via
the [Mermaid Live Editor](https://mermaid.live) or a renderer version that
supports them — this should be verified against the specific tool/version
you're viewing this in.

## Level 1 — System Context

Where FlowX.Upload sits relative to the people and external systems around
it, using the inventory bulk-upload use case as the concrete example.

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

**Reading this diagram:** FlowX.Upload itself is invisible at this level —
it's an internal building block of the Inventory Service, not a separate
system. That's intentional: it's a library, not a service, and shouldn't be
deployed or reasoned about independently.

## Level 2 — Containers

Zooming into the Inventory Service box above.

```mermaid
C4Container
    title Container Diagram — Inventory Service

    Person(manager, "Branch Manager")

    System_Boundary(inventoryService, "Inventory Service") {
        Container(api, "Inventory API", "ASP.NET Core Web API", "Exposes upload start/status/corrections/confirm/retry endpoints")
        Container(orchestrator, "UploadOrchestrator", "FlowX.Upload (in-process)", "Coordinates the session lifecycle")
        Container(worker, "BackgroundServiceUploadExecutor", ".NET BackgroundService", "Runs parse, pre-process, and process work items — single pod")
        Container(reconciler, "StaleSessionReconciler", ".NET IHostedService", "Recovers or fails stale sessions on startup")
        Container(pipeline, "Inventory Upload Pipeline", "Application/Infrastructure layer", "Parser, PreProcessor, Processor, Corrector for this use case")
    }

    ContainerDb(uploadStore, "Upload Session Store", "flowx_upload schema, shared DB", "Durable UploadSession + Plan state")
    ContainerDb(domainDb, "Domain Tables", "Shared DB", "Products, BranchOfferings, ProductDetails")
    System_Ext(providerApi, "Provider Catalog API")

    Rel(manager, api, "HTTP", "REST/JSON, multipart upload")
    Rel(api, orchestrator, "Start / Confirm / SubmitCorrections / Retry", "in-process call")
    Rel(orchestrator, uploadStore, "Save / Get session", "EF Core")
    Rel(orchestrator, worker, "Enqueue work item", "in-memory channel")
    Rel(worker, orchestrator, "Run PreProcessing / Revalidation / Processing", "in-process call")
    Rel(worker, pipeline, "Parse / PreProcess / Process", "in-process call")
    Rel(pipeline, domainDb, "Commit confirmed plan, per chunk, transactional", "EF Core")
    Rel(pipeline, providerApi, "Pre-action: create provider product", "HTTPS")
    Rel(reconciler, uploadStore, "Scan for stale sessions on startup", "EF Core")
    Rel(reconciler, worker, "Re-enqueue recoverable sessions", "in-process call")
```

**Reading this diagram:** the upload session store and the domain tables are
drawn as separate containers even though they may be colocated in the same
physical database (see [docs/design.md](design.md#persistence)) — the
diagram reflects logical/schema ownership, not physical deployment.

## Level 3 — Components (inside FlowX.Upload)

Zooming into the package itself, showing the boundary between what the
package owns and what a consuming application implements.

```mermaid
C4Component
    title Component Diagram — FlowX.Upload Core Library

    Container_Boundary(flowxUpload, "FlowX.Upload (core package)") {
        Component(uploadSession, "UploadSession", "Domain Aggregate", "Guards all lifecycle transitions")
        Component(orchestrator, "UploadOrchestrator", "Application Service", "Sole place orchestration logic lives")
        Component(registry, "IUploadPipelineRegistry", "Application Service", "Resolves a pipeline by string key")
        Component(pipelineFacade, "IUploadPipeline", "Facade", "Type-erased view over one TParsed pipeline")
        Component(storePort, "IUploadStore", "Port", "Persistence abstraction, no assumed technology")
        Component(executorPort, "IUploadExecutor", "Port", "Scheduling abstraction")
        Component(bgExecutor, "BackgroundServiceUploadExecutor", "Infrastructure", "Default single-pod in-memory executor")
        Component(reconciler, "StaleSessionReconciler", "Infrastructure", "Startup recovery sweep")
    }

    Container_Boundary(consumingApp, "Consuming Application") {
        Component(parser, "IUploadParser<T>", "App-defined", "Stage 1: raw file -> parsed shape")
        Component(preProcessor, "IUploadPreProcessor<T>", "App-defined", "Stage 2: validate + build plan")
        Component(corrector, "IUploadCorrector<T>", "App-defined, optional", "Applies UI corrections")
        Component(processor, "IUploadProcessor", "App-defined", "Stage 3: execute confirmed plan")
        Component(efStore, "EfUploadStore", "FlowX.Upload.EntityFrameworkCore", "Reference IUploadStore implementation")
    }

    Rel(orchestrator, uploadSession, "Invokes lifecycle methods on")
    Rel(orchestrator, registry, "Resolves pipeline via")
    Rel(orchestrator, storePort, "Persists / loads session via")
    Rel(orchestrator, executorPort, "Enqueues work via")
    Rel(registry, pipelineFacade, "Returns")
    Rel(pipelineFacade, parser, "Delegates ParseAsync to")
    Rel(pipelineFacade, preProcessor, "Delegates PreProcessAsync to")
    Rel(pipelineFacade, processor, "Delegates ProcessAsync to")
    Rel(pipelineFacade, corrector, "Delegates ApplyCorrections to, if registered")
    Rel(executorPort, bgExecutor, "Default implementation")
    Rel(storePort, efStore, "One possible implementation")
    Rel(reconciler, storePort, "Scans via")
    Rel(reconciler, executorPort, "Re-enqueues via")
```

**Reading this diagram:** everything in the "Consuming Application" boundary
is code the package never contains — this is the line the package must
never cross, per the layering decisions in
[docs/design.md](design.md#layering).
