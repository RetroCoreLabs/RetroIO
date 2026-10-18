# pcap: recordings of the lab machines

The XMSG tests compare our wire model with real traffic. Their finders walk up from the test binary
until they find a folder called `X25Emulator` that holds a `pcap` folder, so these 38 recordings sit here.
This folder is test data. Nothing in it is read by the packages themselves.

**What they are.** Wireshark captures of XMSG, HDLC and SINTRAN traffic between three lab machines,
D100, D102 and D103, made between April and August 2026. A name reads
`YYYY-MM-DD-HHMM-<medium>-<what>-<from>-to-<to>.pcapng`: the date and time are the first packet, `hub-` means
through the COSMOS Ethernet hub (TCP port 5010), `eth-` means raw Ethernet, and no prefix means HDLC frames over a
TCP link between the emulators.

**What was removed.** The captures were first taken on a desktop, so they held unrelated traffic. Everything except
TCP connections that carry valid HDLC frames, hub port 5010 and Norsk Data Ethernet frames was cut out. The decoder
report of the tests is identical before and after.

**What is still in them.**
 - Four recordings contain a SINTRAN login: the `PASSWORD:` prompt and the characters typed after it. The account
   is a test account on a lab machine.
 - File and directory names of test files on the ND machines, such as `RONNY:TXT`.

**These are copies.** The originals, and the notes about each recording, are in a private repository. Add a new
recording there first, then copy it here. A test that names a recording fails when the file is missing, unless
`XMSG_PCAP_OPTIONAL=1` is set.
