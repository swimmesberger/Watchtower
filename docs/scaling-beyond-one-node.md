# Scaling beyond one node

Watchtower is deliberately a **single-host** tool: it registers stacks (a git repo + a compose file),
clones and `docker compose up -d`s them from a UI or a per-stack webhook, streams logs and deploy
output, checks registries for newer images, and updates itself. That model buys radical simplicity —
one Docker daemon, one SQLite file, one image, no cluster to babysit. This document is about the day
that stops being enough, and what the stack looks like on the other side.

Two candidates get examined: **Docker Swarm** (lowest-friction path from compose) and **k3s** (the
recommended path for real multi-node). The scenario throughout is concrete: **~5 physical nodes**
running a small set (1–10) of applications, in a homelab or small-prod setting, where you still want
continuous deployment and a Watchtower-like management/monitoring interface.

> **Verification note.** Project-health and "what ships today" claims below were checked against the
> web in mid-2026 and are marked *(verified)*. See [References](#references). Volatile facts go stale —
> re-check before acting on them.

## 1. Should you even leave single-node?

Most 1–10-app setups **never need multi-node**. Before adopting any orchestrator, be honest about
which of these you actually have:

**Real signals to move:**

- **High availability.** You need the app to survive a node dying — hardware, kernel panic, a bad
  reboot — without manual intervention. A single box can't give you this; nothing about Watchtower can.
- **Resource ceiling.** You've hit the biggest sensible box's RAM/CPU/IO and are still short. Note
  the ceiling is high: 128–256 GB RAM and 32+ cores in one machine is cheap compared to the
  operational cost of a cluster.
- **Blast radius.** One noisy or compromised app can starve or take down everything else on the host,
  and that's unacceptable for your workload mix.
- **Rolling / zero-downtime deploys across replicas.** `compose up -d` recreates a container in place;
  there's a blip. If you need N replicas drained and replaced one at a time behind a load balancer,
  that's an orchestrator feature.

**Reasons to stay single-node (the honest default):**

- A **bigger box + Watchtower** stays dramatically simpler to run than any cluster. No control plane,
  no cluster networking, no distributed storage, no quorum math.
- Vertical scaling covers a startling amount of ground. "We might need HA someday" is not the same as
  needing it now; premature clustering is a common and expensive mistake.
- If your availability requirement is "back within a few minutes after I notice," a second cold box +
  restore-from-backup is cheaper to operate than an HA cluster and fails in ways you understand.

**Rule of thumb:** move off single-node when you need *automatic* failover (HA) or *true* horizontal
scale-out, not merely more compute or nicer deploys. If it's the latter two, buy a bigger box and keep
Watchtower.

## 2. Option A — Docker Swarm

Swarm is the shortest path off single-node because a **stack file is almost a compose file**. If
your mental model and tooling are compose-shaped (Watchtower's are), Swarm asks you to learn the least.

### What carries over from the compose world

- The `docker-compose.yml`/`compose.yaml` format is reused near-verbatim as a **stack file**; you add
  a `deploy:` block (replicas, placement, update policy, resources) and `docker stack deploy -c
  stack.yml <name>`.
- The **same CLI and daemon** you already run. Overlay networks and services replace bare containers,
  but `docker service ls/ps/logs` will feel familiar.
- Secrets and configs become first-class (`docker secret`, `docker config`) instead of bind-mounted
  files — a genuine upgrade over compose.

### CD story

Watchtower's core loop (webhook → clone → deploy) maps cleanly: point your CI webhook at a small
runner that does `git pull && docker stack deploy -c stack.yml <name>`. That is the same shape as
Watchtower's per-stack deploy, just targeting Swarm's convergence engine instead of `compose up`.

For **image-update automation** (Watchtower's registry-check + redeploy behaviour), the Swarm-native
tool is **[Shepherd](https://github.com/containrrr/shepherd)** *(verified: active)* — a Swarm service
that periodically re-resolves each service's image tag and lets Docker roll the update, with optional
`ROLLBACK_ON_FAILURE` and private-registry auth. This is the conceptual analog of what Watchtower's
`StackUpdateService` does.

> **Naming caution.** The well-known container-updater project **`containrrr/watchtower`** (unrelated
> to *this* Watchtower) was **archived on 2025-12-17 and is unmaintained** *(verified)*; the active
> continuation is the fork **`nickfedor/watchtower`**. Neither is Swarm-aware in the way Shepherd is —
> for Swarm, reach for Shepherd, not either watchtower image.

### Management / monitoring UI

- **[Portainer](https://www.portainer.io/)** *(verified: active, actively developed)* is the practical
  answer. It manages Swarm stacks, services, secrets, and gives you logs, stats, and a stack editor —
  the closest thing to a "Watchtower for Swarm," and multi-orchestrator (it also speaks Kubernetes).
- **[Swarmpit](https://swarmpit.io/)** was the lightweight alternative, but it is **effectively
  unmaintained** *(verified — releases stalled for years; issue #607 "status of the project")*. Don't
  build on it.
- Metrics: the classic **cAdvisor + Prometheus + Grafana + node-exporter** stack runs fine as a Swarm
  stack; there's no bundled monitoring.

### Storage gotchas

Swarm has **no built-in multi-node volumes**. A named volume is local to whichever node the task lands
on; reschedule the task and its data is on the wrong host. For any stateful service you must either
**pin it to one node** (`placement.constraints`, which forfeits the HA you came for) or provide shared
storage yourself — **NFS**, **GlusterFS**, or a CSI/volume plugin. This is the single biggest source
of Swarm operational pain and it does not have a turnkey answer.

### Ecosystem trajectory / risk

- Swarm is **maintained, not evolving.** Mirantis (which owns Docker's enterprise business) has
  committed to **supporting Swarm through at least 2030** with a ~6-week security-patch cadence
  *(verified)* — but new orchestration features land in **Mirantis Kubernetes Engine 4, which dropped
  Swarm entirely**; Swarm lives on only in MKE 3 with **no new features planned** *(verified)*. The
  trajectory is clearly toward Kubernetes.
- **Docker Engine v29 (Nov 2025) was disruptive for Swarm operators** *(verified)*: the minimum daemon
  API version was raised (breaking old clients/plugins/tooling), **mixed-version overlay networks can
  silently stop passing encrypted traffic** (forcing an all-at-once, not staggered, node upgrade),
  legacy volume plugins can lose storage access, and Swarm won't run with nftables (needs iptables
  shims). Swarm still works, but it now demands the kind of careful upgrade choreography you adopted an
  orchestrator to avoid.

### Verdict for 5 nodes

Swarm is a **defensible, low-learning-curve choice** if your workloads are mostly stateless and you
value keeping the compose mental model. But you are adopting a technology whose vendor's own roadmap
points at Kubernetes, whose best monitoring UI (Portainer) also speaks Kubernetes, and whose storage
story you have to build yourself. For a *new* 5-node investment in 2026, that's a lot of ceiling for
not much less work than k3s — the migration friction saved today is friction re-paid later.

## 3. Option B — k3s (recommended)

k3s is a certified, ~70 MB single-binary Kubernetes distribution built for exactly this scale (edge,
homelab, small-prod). It costs more to learn than Swarm, but it lands you on the platform everything
else in this space is converging on, with a genuinely better answer for CD, storage, and monitoring.
Here is a concrete reference stack for ~5 physical nodes, one Watchtower responsibility at a time.

### Cluster topology (HA control plane)

For 5 nodes, run **3 server nodes with embedded etcd + 2 agent nodes** *(verified pattern)*. etcd needs
an **odd** number of servers for quorum; 3 servers tolerate 1 failure (quorum = 2). The 2 agents are
pure workload capacity. This gives you an HA control plane — the thing single-node fundamentally can't
offer — while staying small. (Below ~5 nodes, a single server + agents is fine but is *not* HA.)

### GitOps CD: Argo CD vs Flux → **Argo CD**

This is the direct replacement for Watchtower's deploy loop, done right: a controller watches a git
repo and continuously reconciles the cluster to match it (true GitOps, not fire-and-forget webhooks).

- **Flux** is lightweight, CLI/CRD-driven, and headless by default (UIs exist as add-ons). Great for
  air-gapped/edge or platform teams that live in git.
- **Argo CD** ships a **full web UI** — browse apps, see live vs. git diff, sync/rollback from the
  browser *(verified: this is the standard recommendation for teams that want a UI)*.

**Pick Argo CD for this scale.** The whole point of wanting "a management interface like Watchtower" is
a friendly UI a small team can see and operate; Argo CD's app-of-apps view and one-click sync/rollback
is the closest spiritual successor to Watchtower's deploy screen. Webhooks still fit — CI pushes a
manifest/image-tag change to git, Argo CD reconciles — but you also get drift detection Watchtower
never had.

For **automated image bumps** (Watchtower's "newer image?" check), add **Argo CD Image Updater**, which
watches registries and writes new tags back to git. That keeps git as the source of truth while
recovering the ergonomics of Watchtower's `checkUpdates`.

### Management UI ("Watchtower-like"): **Rancher** (or **Headlamp** if you want minimal)

- **[Rancher](https://www.rancher.com/)** *(verified: actively developed by SUSE; v2.12 "Vai" engine is
  production-ready)* is the fullest single-pane-of-glass — cluster health, workloads, logs, projects,
  RBAC, and it can *provision and import* clusters. For a small team that wants "the Watchtower feeling
  for a whole cluster," it's the richest option, at the cost of running Rancher itself.
- **[Headlamp](https://headlamp.dev/)** *(verified: CNCF Sandbox; now the Kubernetes SIG-UI–recommended
  dashboard, maintained under Microsoft/Kinvolk)* is the lighter in-cluster web UI — clean, extensible,
  no cluster-provisioning bulk. A strong default if Rancher feels heavy.
- **Portainer-for-k8s** works and is a natural pick if you know Portainer from Swarm.
- **[k9s](https://k9scli.io/)** / **Lens** are terminal/desktop tools for operators, not shared team
  UIs (k9s is one-context, local-only; the open-source **Lens/OpenLens** line is effectively
  discontinued and now Mirantis-run *(verified)*). Keep **k9s** in your pocket for fast triage; don't
  make it *the* team interface.

**Pick:** Rancher if you want the broadest management surface; Headlamp if you want the smallest thing
that still gives a team a real web UI. Either pairs with Argo CD (which owns *deploys*).

### Monitoring / metrics

- The complete answer is **kube-prometheus-stack** (Prometheus + Grafana + Alertmanager + node/pod
  exporters, via Helm). It is the standard, gives you dashboards and alerting, and is what Rancher's
  monitoring integrates with.
- At 5 nodes this is arguably **more than you need day one.** A lighter ladder: start with
  **metrics-server** (ships-compatible with k3s; powers `kubectl top` and autoscaling) + **k9s** for
  live views, add **Netdata** or a hosted metrics backend if you want pretty graphs without running
  Prometheus, and graduate to kube-prometheus-stack when you actually want alerts. Don't run a full
  Prometheus HA setup for five nodes unless you have a reason.

### Ingress + TLS

k3s **ships Traefik as the default ingress controller** and **ServiceLB (Klipper)** as a bare-metal
load balancer *(verified — k3s bundles containerd, CoreDNS, Traefik, local-path-provisioner, and
metrics-server; k3s ≥1.32 ships Traefik v3)*. Add **cert-manager** for automatic Let's Encrypt certs.
This replaces the "authenticating reverse proxy in front of Watchtower" pattern — you now terminate TLS
and route at the ingress, and put auth (oauth2-proxy/Authelia) there as middleware.

### Load balancing: the HA entry point (Strato and friends)

An HA control plane is only half of HA. If clients reach the cluster through a **single node's IP**, that
node is still a single point of failure — kill it and the site is down no matter how many replicas are
running. You need a **load balancer in front of the nodes** that health-checks them and stops routing to
a dead one. On rented vservers, a **provider load balancer** is the clean way to get it.

**Why not the in-cluster options here.** k3s's bundled **ServiceLB (Klipper)** doesn't give you a real
floating VIP — it just binds the LoadBalancer service's ports (Traefik's 80/443) on *every* node and
reports the node IPs *(verified)*. **MetalLB** and **kube-vip** *can* hand out a true VIP, but they need
**Layer-2 ARP or BGP** on the network — which you usually don't control on rented vservers (the provider
won't let a VM claim an arbitrary floating IP by ARP, and BGP is rarely offered). Rolling your own
nginx/HAProxy on one box just moves the SPoF onto *that* box (you'd then need keepalived/VRRP + a
failover IP to make it HA). So the tidy answer on Strato-style hosting is the provider's **managed load
balancer**, which lives *outside* the cluster and the provider keeps redundant.

**The pattern with Strato's Load Balancer** *(verified against Strato's docs)*. It balances TCP/HTTP/
HTTPS with health checks, gives you a **public IP you point DNS at**, and — conveniently — **does not
terminate TLS** (SSL passthrough only). That keeps the cert-manager story from the previous section
intact: TLS stays in the cluster.

1. Create the LB (Cloud Panel → *Network → Load Balancer*) and **assign all k3s nodes** as targets.
2. **Data plane:** listeners on **:80 (HTTP)** and **:443 (TCP / SSL passthrough)** → the nodes' 80/443,
   where k3s's ServiceLB has Traefik listening; health-check those ports so a dead node is dropped. Point
   your domain's DNS at the LB's public IP. cert-manager/Traefik keep terminating TLS *inside* the
   cluster (ACME HTTP-01 challenges work because :80 is forwarded).
3. **Control plane (recommended):** a listener on **:6443 (TCP)** → the *server* nodes, so `kubectl` and
   node joins survive a server failure. Add the LB's address to every server's **`--tls-san`** so the API
   certificate is valid for it, and use it as the k3s **fixed registration address** *(verified pattern)*.

For your concrete **3-Strato-vserver** case: run all three as k3s servers (embedded etcd, quorum = 2, so
it survives one failure), put the Strato LB in front on 80/443 (and 6443), DNS → LB. Any single node can
die and both the API and your apps stay reachable.

**Caveat — client IPs.** With TCP/SSL passthrough on :443 the app sees the *LB's* address as the source,
not the client's. If you need real client IPs (rate-limiting, geo, audit logs), enable **PROXY protocol**
if the LB supports it, or rely on **X-Forwarded-For** on the HTTP (:80) listener; otherwise it's moot.

### Storage: local disk for Postgres, nothing for the apps

The right storage answer depends entirely on where your state lives, so start from the workload. A
realistic 5-node layout for this app: **1 Postgres primary (writes) + 2 read replicas** (add a third if
read load demands), and **3–5 stateless HTTP pods.** The apps keep **no state on the filesystem** —
everything durable is in Postgres, which replicates itself via streaming WAL. That shape decides
everything below, and it means you do **not** need a distributed/clustered volume system at all.

**Stateless apps → no volumes.** They're plain `Deployment`s: scale replicas freely, reschedule them
anywhere, add an `HorizontalPodAutoscaler` if you like. No PVC, no StorageClass, nothing to persist.
This is the easy 80% and k3s handles it out of the box.

**Postgres → local disk, and let Postgres do the replicating.** The one trap here is putting a
replicating database on top of replicating storage. Because Postgres already streams WAL to its
replicas, running it on **Longhorn/Ceph** means the same bytes are replicated **twice** — a 3-instance
cluster on 3-way Longhorn writes every byte **9×** (3 DB replicas × 3 storage replicas), adds
synchronous replication latency into Postgres's fsync path, and leaves two systems arguing over
durability. This is CloudNativePG's own guidance *(verified)*. So use **node-local storage**: k3s's
built-in **local-path-provisioner** or statically-provisioned **local PersistentVolumes**, one Postgres
pod pinned per node. Local disk is a shared-nothing design — the highest and most predictable database
performance, and exactly what heavy/transactional Postgres wants.

**Run it with a Postgres operator — [CloudNativePG](https://cloudnative-pg.io/)** *(verified: CNCF
Sandbox, accepted Jan 2025)*. It's built for precisely this primary/standby model: native streaming
replication, automated failover, and pod **anti-affinity** so your 3 instances land on 3 different
physical nodes (a node dies → a replica is promoted, no data-layer heroics). It exposes two Services —
**`<cluster>-rw`** (always routes to the current primary) and **`<cluster>-ro`** (round-robins the
replicas) — which map one-to-one onto your "1 write node / 2 read nodes" split: the app points writes at
`-rw` and reads at `-ro`. Alternatives exist (Zalando's postgres-operator, Crunchy PGO); CNPG is the
modern default and the one that most explicitly recommends local storage.

**Backups are a separate concern from replication.** WAL streaming to replicas gives you HA and
read-scaling, but it is **not a backup** — a bad migration or a `DROP TABLE` replicates to every replica
instantly. Add **continuous WAL archiving + base backups to object storage** (MinIO on the cluster, or
external S3); CNPG does this natively and gives you **point-in-time recovery**. That archive, not the
replicas, is what saves you from human error.

**The "recreate a database volume" concern maps to cluster re-bootstrap, not `volume rm`.** In
single-node Watchtower you wipe a database by deleting its volume and redeploying. Under CNPG you either
re-create the `Cluster` with a fresh `initdb` bootstrap (clean slate) or restore from a backup to a
known point-in-time — both explicit, auditable operations, with no way to nuke the database by
accidentally re-running a deploy.

**Clustered/RWX volumes — skip them unless a specific app needs a shared POSIX filesystem** (e.g. one
service writing user uploads that several replicas must read). With all state in Postgres you don't, so
there's no reason to run Longhorn or NFS as a platform default. If that need ever appears for a single
workload, reach for **NFS** or **Longhorn RWX** *for that one app*, not for the whole cluster.

### Secrets: **Sealed Secrets** (or SOPS)

To keep GitOps honest you can't commit plaintext secrets. For a single small cluster, **Bitnami
Sealed Secrets** is the simplest *(verified recommendation for small/single-cluster)* — encrypt with
the cluster's public key, commit the ciphertext, the in-cluster controller decrypts. **SOPS** (with
`age`) is the better fit if you want per-environment keys, offline/local decryption, or multi-cluster
portability. Pick Sealed Secrets to start; reach for SOPS if those needs appear.

### Compose → manifests migration path

- **`kompose`** converts a compose file to Kubernetes manifests. Treat it as a **starting aid, not the
  output** — it gets you Deployments/Services scaffolding, but you'll rework volumes (→ PVCs), env
  (→ ConfigMaps/Secrets), and ingress by hand.
- Package the result as a **Helm chart** (values-driven, good for the same app across envs) or with
  **Kustomize** (overlays over a base; less templating). Either becomes the git source Argo CD watches.
- Migrate **one stack at a time**: convert, get it healthy in k3s, cut traffic over, retire the compose
  version. Keep Watchtower running the not-yet-migrated stacks in parallel.

### The k3s tax (honest cost)

k3s is real Kubernetes with the training wheels of a small distro, but it is **not** Watchtower-simple.
You take on: YAML manifests instead of one compose file; cluster networking (CNI, ingress, DNS) as
concepts you must understand; a Postgres operator and local-PV/StorageClass lifecycle; etcd backups and
control-plane upgrades; certificate rotation; and more moving parts (Argo CD, a UI, cert-manager,
CloudNativePG, maybe Prometheus) that each need care. Watchtower is one container and a SQLite file;
this is a *platform*. The payoff — HA, rolling deploys, operator-managed Postgres with automated
failover, drift-correcting GitOps, and a converging ecosystem — is worth it **when you genuinely need
those things**, and pure overhead when you don't.

## 4. Others, briefly

- **HashiCorp Nomad** — a capable, simpler-than-Kubernetes scheduler. But it relicensed to **BUSL 1.1
  in Aug 2023** (no longer OSI open source; "Community Edition" under a source-available license), and
  **HashiCorp was acquired by IBM (closed Feb 2025)** *(verified)*, leaving future licensing direction
  uncertain. Smaller ecosystem, thinner off-the-shelf CD/UI/storage answers than k3s. Not the pick when
  a certified-Kubernetes option (k3s) exists at the same footprint.
- **Managed Kubernetes** (EKS/GKE/AKS, or DigitalOcean/Hetzner-managed) — if cloud is acceptable, this
  removes the control-plane/etcd/upgrade burden entirely and pairs perfectly with Argo CD. It's the
  *right* answer if you don't specifically want to own physical nodes. It's excluded here only because
  the premise is **self-hosted on ~5 physical machines**.
- **Full upstream Kubernetes (kubeadm)** — more operational surface than k3s for no benefit at this
  scale. k3s *is* conformant Kubernetes; use it.

## 5. Comparison

| Dimension | Watchtower (single-node) | Docker Swarm | k3s |
| --- | --- | --- | --- |
| **CD** | Built-in: webhook + UI `compose up`; image checks | Webhook → `docker stack deploy`; Shepherd for image updates | Argo CD (GitOps, drift-correcting) + Image Updater |
| **Management UI** | Watchtower itself | Portainer (Swarmpit dead) | Rancher / Headlamp (k9s for triage) |
| **Monitoring** | Container list + live logs | Roll your own (Prometheus/Grafana stack) | metrics-server → kube-prometheus-stack; Rancher-integrated |
| **Storage** | Host volumes (trivial) | No multi-node volumes; NFS/Gluster DIY | Stateless apps: none. Postgres: local disk + CNPG operator (let the DB replicate); RWX only if truly needed |
| **HA** | None (single host) | Yes, if you solve storage | Yes: 3 embedded-etcd servers + agents, fronted by an external/provider LB so the entry point isn't a SPoF |
| **Rolling deploys** | No (in-place recreate) | Yes (`deploy.update_config`) | Yes (native, health-gated) |
| **Ops burden** | Lowest — one container + SQLite | Low-moderate; upgrade choreography (v29 pain) | Highest — a platform to run |
| **Migration from compose** | — (native) | Very low (stack ≈ compose) | Moderate (kompose→Helm/Kustomize, rework storage) |
| **Trajectory** | Fits its niche | Maintained to 2030, *not* evolving | Where the ecosystem is converging |

## 6. Recommendation

**For a new 5-node, self-hosted, small-app deployment that needs HA + continuous deployment: choose
k3s.** Swarm saves you learning cost this quarter and repays it as friction later — its vendor roadmap,
best UI, and every serious tool around it point at Kubernetes, and its storage/HA story you have to
assemble yourself. k3s gives you an HA control plane, operator-managed Postgres with automated failover
on plain local disk (CloudNativePG), rolling deploys, and drift-correcting GitOps (Argo CD) with a
friendly web UI (Rancher/Headlamp) — the honest successor to what Watchtower does, at cluster scale.

**But first, re-read §1.** If you don't have a hard HA requirement or a real horizontal-scale need, the
correct answer is still **a bigger box running Watchtower.** Don't pay the k3s tax for deploy
aesthetics or "someday."

**Suggested incremental migration order (once you've decided on k3s):**

1. **Stand up the cluster.** 3 servers (embedded etcd) + 2 agents. Confirm HA by killing a server.
2. **Ingress + TLS + the load balancer.** Keep k3s's Traefik; add cert-manager; move auth to the ingress
   (oauth2-proxy/Authelia) — the reverse-proxy role Watchtower relied on. Provision an external/provider
   load balancer (e.g. Strato's) in front of all nodes on 80/443 (and 6443 for the API), health-checked,
   with DNS pointed at it — so a dead node never blackholes traffic. Confirm HA again by killing a node
   while curling the site.
3. **GitOps + a stateless app first.** Install Argo CD; put one *stateless* HTTP app's manifests in git
   (Deployment + Service + Ingress, no PVC) and let Argo CD own it. Prove the whole loop with the easy
   case before any database is involved.
4. **A management UI + metrics.** Add Rancher or Headlamp; start with metrics-server, add
   kube-prometheus-stack when you want alerts.
5. **Postgres via CloudNativePG on local disk.** Install the CNPG operator; define a `Cluster` (1
   primary + 2 replicas) on a local-path/local-PV StorageClass with pod anti-affinity; wire apps to the
   `-rw`/`-ro` Services. Configure WAL archiving to object storage and **test a point-in-time restore**
   before you trust it.
6. **Migrate the remaining apps**, one at a time, running Watchtower for the not-yet-moved stacks in
   parallel until the last one is cut over. (Most are stateless — the hard part is just the database in
   step 5.)
7. **Secrets + image automation.** Add Sealed Secrets and Argo CD Image Updater to close the loop back
   to Watchtower's `checkUpdates` ergonomics.

Retire Watchtower only when the last compose stack has moved. There's no shame in a hybrid period —
Watchtower on the old box, k3s taking new and stateless workloads first.

## References

Checked mid-2026; these facts drift — re-verify before acting.

- Docker Swarm long-term support (Mirantis, through 2030): mirantis.com/blog/mirantis-guarantees-long-term-support-for-swarm
- MKE 4 drops Swarm / Swarm feature-frozen in MKE 3: virtualizationhowto.com "Is Docker Swarm Still Safe in 2026?"
- Docker Engine v29 Swarm-breaking changes: portainer.io/blog/technical-advisory-docker-swarm; docs.docker.com/engine/release-notes/29
- Swarmpit unmaintained: github.com/swarmpit/swarmpit/issues/607
- `containrrr/watchtower` archived 2025-12-17; fork `nickfedor/watchtower`: github.com/containrrr/watchtower/discussions/2135
- Shepherd (Swarm image updater): github.com/containrrr/shepherd
- Argo CD vs Flux (UI recommendation): northflank.com/blog/flux-vs-argo-cd
- k3s packaged components (Traefik/ServiceLB/local-path/metrics-server; Traefik v3 on ≥1.32): docs.k3s.io/networking/networking-services, docs.k3s.io/installation/packaged-components
- k3s HA embedded etcd (odd servers, quorum): docs.k3s.io/datastore/ha-embedded
- k3s external load balancer for the API (L4 → :6443, `--tls-san`, fixed registration address): docs.k3s.io/datastore/cluster-loadbalancer; docs.k3s.io/datastore/ha
- ServiceLB (Klipper) exposes on node IPs, not a real VIP; MetalLB/kube-vip need L2 ARP or BGP: docs.k3s.io/networking/networking-services; github.com/k3s-io/k3s/discussions/9255
- Strato Load Balancer (TCP/HTTP/HTTPS, health checks, SSL passthrough — no TLS termination, public IP for DNS): strato.de/faq/server/wie-nutze-ich-den-load-balancer
- CloudNativePG (CNCF Sandbox, accepted Jan 2025; primary/standby, streaming replication, `-rw`/`-ro` Services, WAL archiving/PITR): cloudnative-pg.io; cncf.io/projects/cloudnativepg
- CNPG storage guidance — prefer local storage; block-level replication under a replicating DB causes write amplification (single block replica + pod anti-affinity): cloudnative-pg.io/documentation/current/storage
- Longhorn (CNCF Incubating; replicated block + RWX via NFS) — only if a workload genuinely needs shared/replicated volumes: longhorn.io; docs.k3s.io/storage
- Headlamp (CNCF Sandbox, SIG-UI recommended): headlamp.dev
- Rancher active (SUSE, Vai engine v2.12): rancher.com; documentation.suse.com
- Sealed Secrets vs SOPS for small clusters: stackharbor.com/en/knowledge-base/gitops-secrets-sealed-sops-external
- Nomad BUSL relicense (2023) + IBM/HashiCorp acquisition (closed Feb 2025): hashicorp.com/en/blog/hashicorp-adopts-business-source-license
