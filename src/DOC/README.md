# DOC: test data copied from NDInsight

The tests of the XMSG packages measure the code against a written description of the SINTRAN XMSG
wire and against real captured traffic. Their finders walk up from the test binary until they find a
folder called `DOC`, so this folder holds the part of that data that is public.

| Folder | What it is | Used by |
|---|---|---|
| `protocols/` | The protocol registry: every field and bit of the wire, how well it is known, and the evidence | `ProtocolRegistryConformanceTests`, the FA and TAD codec tests, `GeneratedDocsAreCurrentTests` |
| `captures/FA-READ-WRITE-2026-08-04/` | Text dumps of file-server read, write and list traffic | `NdLinkCaptureConformanceTests` |

**These are copies.** The source of truth is the public NDInsight repository, folder
`SINTRAN/XMSG/DOC`. Change the registry there and copy it here again; do not edit the copy.

The binary recordings that other XMSG tests read (`X25Emulator/pcap`) are **not** in this
repository, because they hold real terminal sessions. Those tests skip when the folder is missing and
`XMSG_PCAP_OPTIONAL=1` is set, and fail otherwise.
