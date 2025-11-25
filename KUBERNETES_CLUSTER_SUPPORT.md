# In-Cluster Kubernetes Support Plan

## Goal
Enable LNUnit tests to run inside a real Kubernetes cluster as a pod, with full pod-to-pod communication and dynamic resource creation.

## Use Cases

1. **Staging/QA Environment Testing**: Run integration tests in a staging cluster
2. **Production Validation**: Smoke tests in production-like environment
3. **CI/CD with Real Kubernetes**: GitLab CI, ArgoCD workflows, etc.
4. **Multi-Cluster Testing**: Test across different cluster configurations

---

## Architecture

### Current State (Local/CI)
```
┌─────────────────┐        ┌──────────────────────┐
│  Developer      │        │  GitHub Actions      │
│  Laptop         │───────→│  Runner (Host)       │
│  (OrbStack)     │        │                      │
└─────────────────┘        └──────────────────────┘
        │                           │
        ├─→ kubectl                 ├─→ kind cluster
        │   (kubeconfig file)       │   (pod IPs not accessible)
        │                           │
        ├─→ Pod IPs accessible ✅   ├─→ Use Docker instead ❌
```

### Target State (In-Cluster)
```
┌──────────────────────── Kubernetes Cluster ────────────────────────┐
│                                                                      │
│  ┌─────────────────────┐                                           │
│  │  LNUnit Test Runner │  (Job/Pod)                                │
│  │  - dotnet test      │                                           │
│  │  - In-cluster auth  │                                           │
│  │  - RBAC permissions │                                           │
│  └──────────┬──────────┘                                           │
│             │                                                        │
│             ├─→ Creates test namespace                              │
│             ├─→ Creates bitcoin pod (miner)                         │
│             ├─→ Creates LND pods (alice, bob, carol)                │
│             ├─→ Creates services for DNS                            │
│             │                                                        │
│             └─→ Connects via pod IPs (10.x.x.x) ✅                 │
│                 or service names (miner.test-ns.svc.cluster.local) │
│                                                                      │
└──────────────────────────────────────────────────────────────────┘
```

---

## Implementation Plan

### Phase 1: Auto-Detect In-Cluster Config

**File**: `LNUnit/Setup/KubernetesOrchestrator.cs`

**Current Constructor:**
```csharp
public KubernetesOrchestrator(string? defaultNamespace = null)
{
    var config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
    config.SkipTlsVerify = true;
    _client = new Kubernetes(config);
    _defaultNamespace = defaultNamespace ?? "default";
}
```

**New Constructor with Auto-Detection:**
```csharp
public KubernetesOrchestrator(string? defaultNamespace = null)
{
    KubernetesClientConfiguration config;

    // Try in-cluster config first (when running as a pod)
    if (IsRunningInCluster())
    {
        config = KubernetesClientConfiguration.InClusterConfig();
        Console.WriteLine("[KubernetesOrchestrator] Using in-cluster configuration");
    }
    else
    {
        // Fallback to kubeconfig file (local development)
        config = KubernetesClientConfiguration.BuildConfigFromConfigFile();
        config.SkipTlsVerify = true; // For dev environments only
        Console.WriteLine("[KubernetesOrchestrator] Using kubeconfig file");
    }

    _client = new Kubernetes(config);
    _defaultNamespace = defaultNamespace ?? "default";
}

private static bool IsRunningInCluster()
{
    // Kubernetes sets these environment variables in pods
    return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST"));
}
```

**Testing**: Create a simple test pod that prints the config type.

---

### Phase 2: RBAC Configuration

**File**: `k8s/rbac.yaml` (new file)

Create RBAC resources for test runner pod:

```yaml
---
apiVersion: v1
kind: ServiceAccount
metadata:
  name: lnunit-test-runner
  namespace: default

---
apiVersion: rbac.authorization.k8s.io/v1
kind: Role
metadata:
  name: lnunit-test-runner
  namespace: default
rules:
# Namespace management
- apiGroups: [""]
  resources: ["namespaces"]
  verbs: ["create", "delete", "get", "list"]

# Pod management
- apiGroups: [""]
  resources: ["pods", "pods/log", "pods/status"]
  verbs: ["create", "delete", "get", "list", "watch"]

# Service management
- apiGroups: [""]
  resources: ["services"]
  verbs: ["create", "delete", "get", "list"]

# Pod exec (for file extraction)
- apiGroups: [""]
  resources: ["pods/exec"]
  verbs: ["create"]

---
apiVersion: rbac.authorization.k8s.io/v1
kind: RoleBinding
metadata:
  name: lnunit-test-runner
  namespace: default
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: Role
  name: lnunit-test-runner
subjects:
- kind: ServiceAccount
  name: lnunit-test-runner
  namespace: default

---
# For multi-namespace testing (optional)
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRole
metadata:
  name: lnunit-test-runner-cluster
rules:
- apiGroups: [""]
  resources: ["namespaces"]
  verbs: ["create", "delete", "get", "list"]

---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: lnunit-test-runner-cluster
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: lnunit-test-runner-cluster
subjects:
- kind: ServiceAccount
  name: lnunit-test-runner
  namespace: default
```

**Apply RBAC:**
```bash
kubectl apply -f k8s/rbac.yaml
```

---

### Phase 3: Test Runner Docker Image

**File**: `Dockerfile.test-runner` (new file)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Copy solution and restore
COPY LNUnit.sln .
COPY LNUnit/LNUnit.csproj LNUnit/
COPY LNUnit.LND/LNUnit.LND.csproj LNUnit.LND/
COPY LNBolt/LNBolt.csproj LNBolt/
COPY LNUnit.Tests/LNUnit.Tests.csproj LNUnit.Tests/

RUN dotnet restore

# Copy source and build
COPY . .
RUN dotnet build LNUnit.Tests/LNUnit.Tests.csproj -c Release

# Run tests
FROM build AS test
WORKDIR /src/LNUnit.Tests

ENV UseKubernetes=true
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

ENTRYPOINT ["dotnet", "test", "--no-build", "--configuration", "Release", "--logger", "console;verbosity=detailed"]
```

**Build:**
```bash
docker build -f Dockerfile.test-runner -t lnunit-test-runner:latest .
```

---

### Phase 4: Kubernetes Job Manifest

**File**: `k8s/test-job.yaml` (new file)

```yaml
apiVersion: batch/v1
kind: Job
metadata:
  name: lnunit-test-run
  labels:
    app: lnunit
    test-run: "true"
spec:
  # Keep completed pods for log inspection
  ttlSecondsAfterFinished: 3600  # 1 hour
  backoffLimit: 0  # Don't retry on failure

  template:
    metadata:
      labels:
        app: lnunit-test-runner
    spec:
      serviceAccountName: lnunit-test-runner
      restartPolicy: Never

      containers:
      - name: test-runner
        image: lnunit-test-runner:latest
        imagePullPolicy: IfNotPresent

        env:
        - name: UseKubernetes
          value: "true"
        - name: DOTNET_CLI_TELEMETRY_OPTOUT
          value: "1"

        # Optional: Filter specific tests
        # args: ["--filter", "FullyQualifiedName~BasicPaymentTest"]

        resources:
          requests:
            memory: "1Gi"
            cpu: "500m"
          limits:
            memory: "2Gi"
            cpu: "2000m"
```

**Run Tests:**
```bash
kubectl apply -f k8s/test-job.yaml

# Watch progress
kubectl logs -f job/lnunit-test-run

# Check results
kubectl get job lnunit-test-run
```

---

### Phase 5: ConfigMap for Test Settings

**File**: `k8s/test-config.yaml` (new file)

```yaml
apiVersion: v1
kind: ConfigMap
metadata:
  name: lnunit-test-config
data:
  appsettings.json: |
    {
      "UseKubernetes": true,
      "Serilog": {
        "MinimumLevel": {
          "Default": "Information",
          "Override": {
            "Microsoft": "Warning",
            "System": "Warning"
          }
        },
        "WriteTo": [
          {
            "Name": "Console",
            "Args": {
              "outputTemplate": "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"
            }
          }
        ]
      }
    }
```

**Mount in Job:**
```yaml
spec:
  template:
    spec:
      containers:
      - name: test-runner
        volumeMounts:
        - name: test-config
          mountPath: /src/LNUnit.Tests/appsettings.json
          subPath: appsettings.json
      volumes:
      - name: test-config
        configMap:
          name: lnunit-test-config
```

---

### Phase 6: Namespace Isolation Strategy

**Current Approach**: Tests create random namespace names

**Enhanced Approach**: Use job-specific namespaces with labels

**Update**: `LNUnitBuilder.cs`

```csharp
private string GenerateNetworkName(string baseName)
{
    // Include pod name if running in cluster for traceability
    var podName = Environment.GetEnvironmentVariable("HOSTNAME") ?? "local";
    var randomHex = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLower();
    return $"{baseName}-{podName}-{randomHex}";
}
```

**Example namespace names:**
- Local: `unit_test_a1b2c3d4`
- In-cluster: `unit_test_lnunit-test-run-abc123-a1b2c3d4`

---

### Phase 7: Cleanup Strategy

**Problem**: Failed tests may leave namespaces/pods behind

**Solution 1: Namespace Labels**

```csharp
public async Task<string> CreateNetworkAsync(string networkName)
{
    var ns = new V1Namespace
    {
        Metadata = new V1ObjectMeta
        {
            Name = namespaceName,
            Labels = new Dictionary<string, string>
            {
                { "lnunit", "true" },
                { "test-run", Environment.GetEnvironmentVariable("HOSTNAME") ?? "local" },
                { "created-at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString() }
            }
        }
    };

    await _client.CoreV1.CreateNamespaceAsync(ns).ConfigureAwait(false);
    return namespaceName;
}
```

**Solution 2: Cleanup CronJob**

**File**: `k8s/cleanup-cronjob.yaml` (new file)

```yaml
apiVersion: batch/v1
kind: CronJob
metadata:
  name: lnunit-cleanup
spec:
  schedule: "0 * * * *"  # Every hour
  jobTemplate:
    spec:
      template:
        spec:
          serviceAccountName: lnunit-test-runner
          restartPolicy: OnFailure
          containers:
          - name: cleanup
            image: bitnami/kubectl:latest
            command:
            - /bin/bash
            - -c
            - |
              # Delete test namespaces older than 1 hour
              CUTOFF=$(date -u -d '1 hour ago' +%s)

              kubectl get namespaces -l lnunit=true -o json | \
              jq -r ".items[] | select(.metadata.labels[\"created-at\"] | tonumber < $CUTOFF) | .metadata.name" | \
              while read ns; do
                echo "Deleting old test namespace: $ns"
                kubectl delete namespace "$ns" --timeout=60s
              done
```

---

### Phase 8: CI/CD Integration

**GitLab CI Example:**

**File**: `.gitlab-ci.yml`

```yaml
stages:
  - build
  - test

variables:
  DOCKER_IMAGE: ${CI_REGISTRY_IMAGE}/test-runner:${CI_COMMIT_SHORT_SHA}

build-test-image:
  stage: build
  image: docker:latest
  services:
    - docker:dind
  script:
    - docker build -f Dockerfile.test-runner -t ${DOCKER_IMAGE} .
    - docker push ${DOCKER_IMAGE}

test-kubernetes:
  stage: test
  image: bitnami/kubectl:latest
  script:
    # Update image in job manifest
    - sed -i "s|lnunit-test-runner:latest|${DOCKER_IMAGE}|g" k8s/test-job.yaml

    # Apply RBAC (idempotent)
    - kubectl apply -f k8s/rbac.yaml

    # Run test job
    - kubectl apply -f k8s/test-job.yaml

    # Wait for completion (30 min timeout)
    - kubectl wait --for=condition=complete --timeout=1800s job/lnunit-test-run

    # Get logs
    - kubectl logs job/lnunit-test-run

    # Check if successful
    - kubectl get job lnunit-test-run -o jsonpath='{.status.succeeded}' | grep -q 1
  after_script:
    # Cleanup job
    - kubectl delete job lnunit-test-run --ignore-not-found=true
  only:
    - merge_requests
    - main
```

**GitHub Actions Example:**

**File**: `.github/workflows/test-in-cluster.yml`

```yaml
name: Test in Cluster

on:
  workflow_dispatch:
  schedule:
    - cron: '0 2 * * *'  # Nightly

jobs:
  test-in-cluster:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Set up kubeconfig
        run: |
          mkdir -p ~/.kube
          echo "${{ secrets.KUBECONFIG }}" | base64 -d > ~/.kube/config

      - name: Build test image
        run: |
          docker build -f Dockerfile.test-runner -t lnunit-test-runner:${{ github.sha }} .

          # Push to registry accessible by cluster
          # docker push ...

      - name: Apply RBAC
        run: kubectl apply -f k8s/rbac.yaml

      - name: Run tests
        run: |
          # Update image tag
          sed -i "s|lnunit-test-runner:latest|lnunit-test-runner:${{ github.sha }}|g" k8s/test-job.yaml

          kubectl apply -f k8s/test-job.yaml
          kubectl wait --for=condition=complete --timeout=30m job/lnunit-test-run

      - name: Get test logs
        if: always()
        run: kubectl logs job/lnunit-test-run

      - name: Cleanup
        if: always()
        run: kubectl delete job lnunit-test-run --ignore-not-found=true
```

---

## Testing Plan

### Local Testing
1. Use kind or minikube (not OrbStack for this test)
2. Build test image locally
3. Load image into cluster: `kind load docker-image lnunit-test-runner:latest`
4. Apply RBAC: `kubectl apply -f k8s/rbac.yaml`
5. Run job: `kubectl apply -f k8s/test-job.yaml`
6. Watch logs: `kubectl logs -f job/lnunit-test-run`

### Staging Cluster Testing
1. Deploy to staging cluster
2. Run subset of tests first
3. Verify cleanup works
4. Run full test suite

### Production Validation
1. Use separate service account with read-only permissions where possible
2. Run smoke tests only
3. Implement stricter timeouts

---

## Benefits

1. **Realistic Environment**: Tests run in actual Kubernetes
2. **Network Policies**: Test with real security constraints
3. **Resource Limits**: Test with actual QoS and resource constraints
4. **Cloud Integration**: Test with cloud-specific features (LoadBalancers, StorageClasses)
5. **Multi-Cluster**: Can run same tests across dev/staging/prod
6. **No Networking Hacks**: Pod-to-pod communication just works

---

## Limitations & Considerations

1. **Cost**: Running tests consumes cluster resources
2. **Permissions**: Requires elevated RBAC permissions
3. **Cleanup**: Failed tests may leave resources behind
4. **Speed**: Slightly slower than Docker due to pod scheduling
5. **Isolation**: Tests must not interfere with production workloads

---

## Migration Path

**Current State**:
- Local dev: OrbStack (pod IPs accessible)
- CI/CD: Docker (simple, fast)

**Target State**:
- Local dev: OrbStack (no change)
- CI/CD (kind): Docker (no change)
- CI/CD (real cluster): In-cluster (new capability)
- Production validation: In-cluster (new capability)

**No breaking changes** - existing workflows continue to work!

---

## Success Criteria

✅ Tests run successfully in a real cluster
✅ Pod-to-pod communication works via pod IPs
✅ Service DNS resolution works
✅ Namespaces are created and cleaned up properly
✅ RBAC permissions are minimal and secure
✅ Failed tests don't leave resources behind
✅ CI/CD pipeline can run tests in cluster
✅ Logs are accessible for debugging

---

## Open Questions

1. Should we support persistent storage for bitcoin data between test runs?
2. Should we implement test parallelization with multiple namespaces?
3. Should we add Prometheus metrics for test execution?
4. Should we support network policies to test restricted environments?
