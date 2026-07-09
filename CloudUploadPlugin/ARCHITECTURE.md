# Cloud Upload Plugin - Architecture & Design Document (SOTA Open Mediator Pattern)

## 1. Overview & Strategy
This architecture implements the **"Open Mediator" Pattern**.
*   **The Plugin**: Acts as a lightweight, open-source client. It handles the N.I.N.A. integration, event listening, and data transmission.
*   **The Service**: A closed-source, commercial cloud platform. It handles the proprietary business logic, storage, processing, and user management.
*   **Security Model**: Zero-trust client. The plugin is untrusted; all security is enforced by the Cloud API via X-API-Key authentication.

## 2. Core Components

### 2.1. N.I.N.A. Integration Layer
*   **Manifest (`AssemblyInfo.cs` + `PluginManifest.cs`)**:
    *   Fully compliant with N.I.N.A. Repo Policy.
    *   `Repository` links to the public GitHub repo (Mediator code).
    *   `License` uses MPL 2.0 (compatible with N.I.N.A.).
*   **Event Listener (`ImageSavedHandler`)**:
    *   Subscribes to `IImageSaveMediator.ImageSaved`.
    *   **Logic**:
        1.  Receive `ImageSavedEventArgs` (Path: `.../2025/M31/Light/001.fits`).
        2.  Resolve relative path structure (Key feature).
        3.  Push `UploadJob` to the Transfer Service.

### 2.2. Configuration & State Management (`PluginOptionsAccessor`)
*   **Storage**: Uses N.I.N.A.'s `IPluginOptionsAccessor` to store user-specific settings securely.
*   **Settings Persisted**:
    *   `ApiKey` (Encrypted via DPAPI).
    *   `UploadEnabled` (Bool).
    *   `BandwidthLimit` (Int).

### 2.3. User Interface (MVVM Pattern)
*   **`Options.xaml` (View)**:
    *   Embedded in N.I.N.A.'s Options tab.
    *   **Login Flow**: A "Connect with [CloudName]" button.
    *   **Dashboard**: Displays "User: [Name]", "Storage Used: 45%", "Queue Status".
*   **`OptionsVM` (ViewModel)**:
    *   Handles UI logic.
    *   Handles API key entry and validation via PasswordBox input.

### 2.4. Transfer Service (The Engine)

*   **`UploadManager`**:

    *   **Persistent Queue**: Uses a local **SQLite/LiteDB** or file-backed journal (`ConcurrentQueue` is insufficient for production). This ensures uploads survive N.I.N.A. restarts or crashes.

    *   **Flow Control**: Implements a **Bounded Queue** pattern. If the network is down and the local backlog exceeds N items (e.g., 50GB), the plugin pauses queueing to prevent disk exhaustion and notifies the user.

    *   **Resiliency**: Implements **Exponential Backoff** for retries.

    *   **Chunked Uploads**: For large FITS/XISF files (50MB+), splits files into chunks.

*   **`ApiClient`**:

    *   Typed `HttpClient` implementation.

    *   **Circuit Breaker**: Detects API downtime and pauses uploads.



## 3. Security Architecture (X-API-Key Authentication)

The plugin uses a simple API key authentication model:

1.  **Key Entry**:
    *   User pastes their API key into the plugin settings UI (PasswordBox for security).
    *   Plugin validates the key via `GET /api/auth/validate` with `X-API-Key` header.

2.  **Key Storage**:
    *   Validated key is encrypted using Windows DPAPI (CurrentUser scope) and stored in `auth.dat`.
    *   DPAPI ensures the key is only readable by the same Windows user on the same machine.

3.  **Request Authentication**:
    *   Every API request includes `X-API-Key: [key]` header.
    *   Server validates the key and returns 401 Unauthorized for invalid keys.

4.  **Key Lifecycle**:
    *   **Store**: After successful validation via `/api/auth/validate`.
    *   **Load**: On plugin startup, decrypts from `auth.dat`.
    *   **Clear**: User clicks "Disconnect" -- deletes `auth.dat` and resets to NotConfigured.
    *   **Recovery**: If DPAPI decryption fails (OS reinstall, profile switch), `auth.dat` is deleted and user re-authenticates.



## 4. Folder Structure (Maven/Standard Layout)



```text

CloudUploadPlugin/

├── Properties/

│   └── AssemblyInfo.cs          # Manifest Metadata

├── Manifest/

│   └── CloudPluginManifest.cs   # IPluginManifest impl

├── UI/

│   ├── Options.xaml             # Settings UI

│   └── OptionsVM.cs             # ViewModel (Login Logic)

├── Integration/

│   └── ImageSaveListener.cs     # N.I.N.A. Event Hooks

├── Core/

│   ├── UploadManager.cs         # Persistent Queue Engine

│   ├── AuthManager.cs           # API Key Storage (DPAPI)

│   └── PathResolver.cs          # Structure Mirroring

├── Data/

│   └── UploadQueueRepository.cs # SQLite/File-backed storage

├── Networking/

│   └── CloudApiClient.cs        # REST API Wrapper

└── Models/

    └── UploadJob.cs             # DTO

```



## 5. Development Roadmap (Phased)



1.  **Phase 1: The Skeleton** (Current)

    *   Setup Project Structure.

    *   Implement Manifest & UI placeholders.

    *   Verify N.I.N.A. loads the plugin.



2.  **Phase 2: The Glue**

    *   Implement `ImageSaveListener`.

    *   Verify file paths are captured correctly.



3.  **Phase 3: The Engine**

    *   Implement `UploadManager` (File-backed Queue).

    *   Test queueing system with network disconnection simulation.



4.  **Phase 4: The Security**

    *   Implement API Key authentication with DPAPI storage.

    *   Connect to Real Cloud API.



## 6. Technical Implementation Details



### 6.1. IImageSaveMediator Analysis

The plugin utilizes the `IImageSaveMediator` to interact with the N.I.N.A. image lifecycle. 

*   **Primary Hook (`ImageSaved`)**: Provides the absolute path on disk.

*   **Pre-Processing (`BeforeImageSaved`)**: Optional metadata modification.



### 6.2. Concurrency and Threading

*   **N.I.N.A. Pattern**: Uses `AsyncProducerConsumerQueue`.

*   **Plugin Pattern**: We decouple strictly. The event handler performs a ~1ms "Write to DB" operation. A separate `Task` polls the DB and handles the slow network upload.



### 6.3. File Access

*   **Non-Exclusive Access**: Images opened with `FileShare.Read`.



## 7. Risk Mitigations



### 7.1. Queue Bound & Persistence (High Risk)

*   **Problem**: In-memory queues grow unbounded during network outages, risking OOM crashes.

*   **Mitigation**:

    1.  **Spill-to-Disk**: All jobs are serialized to a local `jobs.db` immediately.

    2.  **Capacity Limit**: If `jobs.db` > 10GB (configurable), the plugin logs an error and stops queueing new files to protect the user's OS disk.

    3.  **Recovery**: On N.I.N.A. startup, the plugin reads `jobs.db` and resumes pending uploads.



### 7.2. Path Stability (Medium Risk)

*   **Problem**: Interface documentation suggests the file might move after the `ImageSaved` event (e.g., temp to final rename).

*   **Mitigation**:

    *   **Stability Check**: Before uploading, the `UploadManager` performs a sanity check: `File.Exists(path)` and optionally waits 500ms to ensure no file locks are held by a move operation.

    *   **Directory Watching (Backup)**: If `ImageSaved` proves unreliable for the final path, we can fallback to a `FileSystemWatcher` on the user's Root Directory, correlating new files with the capture event timestamp.



### 7.3. API Key & DPAPI (Low Risk)

*   **Problem**: DPAPI data is lost on OS reinstall or user profile switch.

*   **Mitigation**:

    *   **Graceful Degrade**: If decryption fails, the `AuthManager` catches the exception, clears the invalid API key, and sets the UI state to "Disconnected".

    *   **Re-Auth Prompt**: The User is shown a "Session Expired - Please Login Again" message rather than a silent crash.