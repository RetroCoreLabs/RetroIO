//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Buffers;

namespace RetroIO.Protocols.Tn3270
{
    /// <summary>
    /// Why the keyboard is locked.
    /// </summary>
    public enum KeyboardLock
    {
        /// <summary>
        /// Not locked.
        /// </summary>
        None,

        /// <summary>
        /// An attention key was sent and no Write has come back yet: TWAIT, shown as X and a
        /// clock. The operator's Reset key does NOT clear this (GA23-0059-4 p. 3-20).
        /// </summary>
        WaitingForHost,

        /// <summary>
        /// The host has written since the attention key but did not restore the keyboard: System
        /// Lock, shown as "X SYSTEM". Reset clears it, as does a Write with keyboard restore.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4 p. 3-20: System Lock is raised with TWAIT by every enter action, is not
        /// shown while TWAIT exists, and unlike TWAIT can be cleared by Reset; GA27-2890-4 p. 2-21
        /// describes "X SYSTEM" the same way. x3270 makes the change at the end of a Write without
        /// keyboard restore (Common\ctlr.c, ctlr_write: KL_OIA_TWAIT cleared, status_syswait),
        /// which is the rule here.
        /// </remarks>
        SystemLock,

        /// <summary>
        /// The operator did something not allowed, such as typing into a protected field
        /// ("Go Elsewhere", GA27-2890-4 p. 2-21). Reset clears it.
        /// </summary>
        /// <remarks>
        /// IBM locks here; x3270 by default only ignores the key. RetroTerm follows IBM (decided
        /// 22 November 2026).
        /// </remarks>
        OperatorError,

        /// <summary>
        /// Insert mode found no null left in the field to take the character ("Too Much Data",
        /// GA27-2890-4 p. 3-42; GA23-0061-2 p. C-5). Reset clears it.
        /// </summary>
        TooMuchData,

        /// <summary>
        /// The cursor tried to leave, or the operator tried to send, a mandatory-fill field that has
        /// data in it but is not full (GA23-0059-4 p. 4-21). Reset clears it.
        /// </summary>
        MandatoryFill,

        /// <summary>
        /// The operator tried to send while a mandatory-entry field is still empty; the cursor was
        /// moved to it (GA23-0059-4 p. 4-22). Reset clears it.
        /// </summary>
        MandatoryEntry,

        /// <summary>
        /// A key whose function is not available now: "Minus Function", shown as "X -f". Reset
        /// clears it, as does a Write with keyboard restore.
        /// </summary>
        /// <remarks>
        /// GA27-2890-4 p. 3-32: Attn works only when the OIA shows the "my job" symbol, otherwise
        /// Minus Function. x3270 sets it for Attn on a TN3270E connection with no SNA session
        /// bound (Common\kybd.c, Attn_action: kybdlock_set(KL_OIA_MINUS)) and shows "X -f"
        /// (Common\vstatus.c). x3270 keeps its locks as bits and can hold Minus Function and TWAIT
        /// together; here the lock is one value, so Minus Function replaces whatever was there.
        /// The states a person can then reach are the same: Reset clears both in x3270 and clears
        /// this one here, and a Write with keyboard restore clears all of them in both.
        /// </remarks>
        MinusFunction,

        /// <summary>
        /// The terminal has sent its query replies (Read Partition Query or Query List) and the host
        /// has not acknowledged them yet: TWAIT, shown as "X Wait". No keystroking; the operator's
        /// Reset key does NOT clear it. The host's next outbound transmission that is not a read
        /// command clears it.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4, Read Partition Query processing for BSC (p. 9-9) and for locally attached
        /// non-SNA devices (p. 10-8): "The TWAIT indicator is displayed", and after the reply is
        /// sent "the device [is] placed in retry-read state"; the indicator tables give TWAIT for
        /// Retry read (p. 9-6, p. 10-5). TWAIT is an input inhibit condition, "no keystroking is
        /// allowed", and "cannot be cleared by the operator (for example, by the RESET Key)"
        /// (p. 3-20). The SNA chapter says only that a Read Partition puts the device in retry-read
        /// state, where an enter action is rejected (p. 3-17, 3-18); it does not name an
        /// indicator. What ends it (p. 3-23, p. 10-5): "a Query or Query List operation is
        /// acknowledged by any outbound transmission except one with a read command" - a WSF even
        /// when its structured fields hold an error, an EAU, and an EW, EWA or Write with or without
        /// a WCC unless an error is found in its data. Keyboard restore is not needed. x3270 locks
        /// here too (Common\sf.c, query_reply_end: kybd_inhibit(true), shown "X Wait" by
        /// Common\vstatus.c) and unlocks on the next Write, Erase/Write or EAU (Common\ctlr.c); its
        /// Reset key also unlocks it, which the manual's TWAIT rule does not allow, so here it does
        /// not. Unlike <see cref="WaitingForHost"/>, a Write without keyboard restore does not
        /// leave System Lock behind: System Lock is raised only by an enter action (p. 3-21).
        /// </remarks>
        QueryReplySent,

        /// <summary>
        /// PWAIT: the active partition sent an enter action the host has not acknowledged, and
        /// there is more than one partition. Shown as "X", arrows and a clock; the arrows say the
        /// operator may jump to another partition and type there. Reset does NOT clear it.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4 p. 2-9, 3-20: "TWAIT will be used when in the implicit partition state or
        /// when there is only one explicit partition. PWAIT is used when there is more than one
        /// partition". The indicator for TWAIT is "X Clock"; for PWAIT it is X, then the clock
        /// between two arrows pointing outwards. With one partition the same condition is <see cref="WaitingForHost"/>. The condition
        /// belongs to the partition (<see cref="Partition3270.PartitionWait"/>): after a jump to
        /// another partition the indicator goes, and it comes back with the partition.
        /// </remarks>
        PartitionWait,

        /// <summary>
        /// The host gave a negative reply to a trigger field (GA23-0059-4 p. 4-24: "the
        /// invalid-field input-inhibit condition is raised"). Reset clears it.
        /// </summary>
        InvalidField,

        /// <summary>
        /// Keystrokes queued while the host validates a trigger field filled the queue
        /// (GA23-0059-4 p. 4-25 note 4: "If the keystroke queue becomes full before a write is
        /// received from the host, an input-inhibit condition is raised"). Reset clears it.
        /// </summary>
        /// <remarks>
        /// The manual names neither the queue's size nor which condition, nor what clears it.
        /// The size (<see cref="Display3270.KeystrokeQueueCapacity"/>) and Reset clearing it are
        /// this display's own choices.
        /// </remarks>
        KeystrokeQueueFull,
    }

    /// <summary>
    /// A 3270 display station screen: buffer, fields, attributes, cursor and keyboard lock, with
    /// the host side (outbound records) and the operator side (keys) both handled here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No network and no window. A host record goes in through <see cref="ProcessOutbound"/>; what the
    /// terminal sends back comes out through <see cref="InboundRecord"/>; a renderer reads the
    /// buffer through <see cref="GetByte"/>, <see cref="IsFieldAttribute"/> and
    /// <see cref="GetFieldAttributeFor"/>.
    /// </para>
    /// <para>
    /// Write Structured Field is handled for Read Partition (Query, Query List, and the three reads
    /// of any partition), Erase/Reset, Set Reply Mode, Outbound 3270DS, Create Partition, Destroy
    /// Partition, Activate Partition, Reset Partition and the file transfer field X'D0', which goes
    /// to <see cref="FileTransferField"/>.
    /// </para>
    /// <para>
    /// Partitions (GA23-0059-4 chapter 2): up to <see cref="MaxPartitions"/> explicit partitions,
    /// each with its own presentation space, cursor, reply mode and 12/14-bit or 16-bit
    /// addressing, shown in non-overlapping viewports. A presentation space may have more rows
    /// than its viewport (vertical scrolling, p. 2-3, 7-16): Set Window Origin, the scroll keys
    /// (<see cref="ScrollUp"/>, <see cref="ScrollDown"/>) and automatic scrolling move the window.
    /// The getters such as <see cref="GetByte"/> read the active partition;
    /// <see cref="GetPartition"/> and <see cref="TryGetScreenPosition"/> reach the others.
    /// </para>
    /// <para>
    /// Input inhibit (p. 2-9, 3-14 to 3-21): PWAIT and System Lock belong to a partition and are
    /// remembered when another partition is made active; Enter Inhibit, the read state (normal,
    /// retry enter, retry read), INPID and INOP belong to the display. The jump-partition key is
    /// <see cref="JumpPartition"/>. Trigger fields (p. 4-23 to 4-26) are built, with the queue of
    /// keystrokes made while the host validates one.
    /// </para>
    /// <para>
    /// Not built yet, and loud about it through <see cref="ProtocolError"/>: horizontal
    /// scrolling, Modify Partition, text partitions, all-points units, protected partitions,
    /// every other structured field, and the BSC Copy command inside Outbound 3270DS. Load
    /// Programmed Symbols is built, see <see cref="ProgrammedSymbols"/>.
    /// </para>
    /// </remarks>
    public sealed class Display3270
    {
        // EBCDIC code page 037 characters the selector-pen designator and Field End test for
        // (GA23-0059-4 p. 4-22: "?" X'6F', ">" X'6E', "&" X'50', space X'40').
        private const byte EbcdicSpace = 0x40;
        private const byte EbcdicQuestion = 0x6F;
        private const byte EbcdicGreater = 0x6E;
        private const byte EbcdicAmpersand = 0x50;

        // SCS New Line, the one control code of SSCP-LU text (GA23-0061-2 p. 5-37).
        private const byte ScsNewLine = 0x15;

        // The SSCP-LU limits of a 3274: 1,536 bytes outbound, an inbound RU of 256 (p. 5-37).
        private const int MaxSscpOutbound = 1536;
        private const int MaxSscpInbound = 256;

        // The character attribute types an SA order may carry (p. 4-16): highlighting, foreground
        // colour, character set, background colour, transparency.
        private static readonly byte[] CharacterAttributeTypes = { 0x41, 0x42, 0x43, 0x45, 0x46 };

        /// <summary>
        /// The local ID the Character Sets query reply gives the base character set (GA23-0059-4
        /// p. 6-26: X'F0' to X'F7' for non-loadable 1-byte sets; X'F1' is the Graphic Escape set).
        /// </summary>
        public const byte BaseCharacterSetId = 0xF0;

        /// <summary>
        /// The local ID the Character Sets query reply gives the Graphic Escape set, code page 310.
        /// </summary>
        public const byte GraphicEscapeCharacterSetId = 0xF1;

        /// <summary>
        /// How many explicit partitions this display takes, named 0 to MaxPartitions - 1: the NA
        /// of the Alphanumeric Partitions query reply (GA23-0059-4 p. 6-18, 6-19).
        /// </summary>
        /// <remarks>
        /// The manual lets a device name any count up to the 127 PIDs X'00' to X'7E' (p. 5-10); it
        /// does not give a count for an emulated one. 16 is this display's own choice, not a value
        /// from the manual or from another emulator (x3270 and tnz build no partitions).
        /// </remarks>
        public const int MaxPartitions = 16;

        /// <summary>
        /// How many keystrokes are queued while the host validates a trigger field before the
        /// <see cref="KeyboardLock.KeystrokeQueueFull"/> condition is raised (GA23-0059-4 p. 4-25
        /// note 4).
        /// </summary>
        /// <remarks>
        /// The manual gives no size. 64 is this display's own choice.
        /// </remarks>
        public const int KeystrokeQueueCapacity = 64;

        // M of the Alphanumeric Partitions reply, the bytes of partition storage (p. 6-18, 6-19):
        // with scrolling a presentation space may be larger than the screen, so the storage is
        // not tied to the screen size. 32767 is this display's own choice, not a value from the
        // manual (X'FFFF' is reserved there); a 12/14-bit partition is still held to 16384 cells
        // by its addresses (p. 4-4).
        private const int PartitionStorage = 0x7FFF;

        // Structured field IDs of the partition structured fields (GA23-0059-4 p. 5-2).
        private const byte ResetPartitionId = 0x00;
        private const byte SetWindowOriginId = 0x0B;
        private const byte CreatePartitionId = 0x0C;
        private const byte DestroyPartitionId = 0x0D;
        private const byte ActivatePartitionId = 0x0E;

        // INOP for a Read Partition Query or Query List (p. 3-19); the other INOP values are the
        // read commands' own codes.
        private const byte InopQuery = 0x02;

        // The query replies this terminal sends, in ascending code order (p. 6-81: Null is never listed).
        private static readonly byte[] QueryCodesWithoutFileTransfer =
        {
            QueryCode.Summary, QueryCode.UsableArea, QueryCode.AlphanumericPartitions, QueryCode.CharacterSets, QueryCode.Color,
            QueryCode.Highlighting, QueryCode.ReplyModes, QueryCode.FieldValidation, QueryCode.FieldOutlining, QueryCode.ImplicitPartition,
        };

        private static readonly byte[] QueryCodesWithFileTransfer =
        {
            QueryCode.Summary, QueryCode.UsableArea, QueryCode.AlphanumericPartitions, QueryCode.CharacterSets, QueryCode.Color,
            QueryCode.Highlighting, QueryCode.ReplyModes, QueryCode.FieldValidation, QueryCode.FieldOutlining, QueryCode.Ddm, QueryCode.ImplicitPartition,
        };

        private readonly int _defaultRows;
        private readonly int _defaultColumns;
        private readonly int _alternateRows;
        private readonly int _alternateColumns;

        // The sizes Erase/Write, Erase/Write Alternate, Erase/Reset and Clear use now. They are
        // the model's own (the four fields above, which the Query Reply always reports, as x3270
        // reports its model in Common\sf.c) until a BIND sets others for the session
        // (ApplyBind, GA23-0061-2 Figure 1-11), and the model's again after an UNBIND (Unbind).
        private int _sessionRows;
        private int _sessionColumns;
        private int _sessionAlternateRows;
        private int _sessionAlternateColumns;

        // SSCP-LU mode: the initial cursor address, where the operator's message to the SSCP
        // starts (GA23-0061-2 p. 5-37, "Outbound Message Handling").
        private int _sscpStart;

        private readonly ArrayBufferWriter<byte> _inbound = new ArrayBufferWriter<byte>(4096);

        // The inbound record of the inbound operation in progress (an enter action, a trigger
        // action, a Read Partition, or SendStructuredFieldReply(field, retry: true)), kept until
        // the host acknowledges it, so a Read Modified command in a retry state sends it again
        // (p. 3-17, 3-18). _retryLength 0: nothing kept. Kept only while _readState is not
        // Normal.
        private byte[] _retryRecord = Array.Empty<byte>();
        private int _retryLength;

        // The read state (p. 3-17 to 3-19, Figure 3-1) and INOP (p. 3-19). In either retry state
        // the display has the Enter Inhibit condition: no enter action is accepted from any
        // partition (p. 3-20, 3-21), and a Read Partition is rejected with X'0871' (p. 3-18).
        private ReadState _readState;
        private byte _inop = Command3270.ReadModified;

        // The input inhibit conditions that belong to the display rather than to a partition:
        // operator errors, Minus Function, the query TWAIT and the trigger conditions. Activate
        // Partition leaves them shown (p. 5-8 step 5b: "All other input-inhibit indicators remain
        // displayed"). PWAIT and System Lock are in each Partition3270.
        private KeyboardLock _deviceLock;

        // A trigger action waiting for the host's reply (p. 4-23, 4-24): the partition, where the
        // keystroke that caused it would have put the cursor (and the window, for a scroll key),
        // whether that target wins over an Insert Cursor in the reply and raises Mandatory Entry
        // (the case of p. 4-25 note 2), and the queued keystrokes.
        private bool _triggerPending;
        private byte _triggerPid;
        private int _triggerTarget;
        private int _triggerWindowRow = -1;
        private bool _triggerForMandatoryEntry;
        private readonly Keystroke[] _queue = new Keystroke[KeystrokeQueueCapacity];
        private int _queueCount;

        // Set by an Insert Cursor order in the Write being applied, so an affirmative trigger
        // reply knows whether the host placed the cursor (p. 4-23).
        private bool _insertCursorSeen;

        // What the record being applied is to a pending trigger action (p. 4-24), decided before
        // it is applied.
        private TriggerReply _triggerReply;

        // The partitions (GA23-0059-4 chapter 2). The buffers, the attributes, the cursor, the
        // reply mode and the primed mandatory-fill field of each are in its Partition3270 (the
        // comments on what each array holds are there).
        //
        // _implicitPartition is implicit partition 0, the only one in implicit partition state.
        // _partitions holds the explicitly created ones, by PID, in explicit partitioned state
        // (p. 2-6, 2-7); it is all null in implicit partition state.
        //
        // _p is the partition the code below works on: the active partition whenever the
        // operator's keys run, and, while a host record is applied, the partition the record
        // names (an Outbound 3270DS's PID, explicit partition 0 for a command sent as the first
        // byte). After every host record it is the active partition again.
        private readonly Partition3270 _implicitPartition;
        private readonly Partition3270?[] _partitions = new Partition3270?[MaxPartitions];
        private bool _explicitState;
        private Partition3270 _p;

        // The active partition, the one the operator works in (p. 2-5), and INPID, the partition
        // inbound data comes from and a retry repeats (p. 3-20).
        private byte _activePid;
        private byte _inboundPid;

        // The usable area of explicit partitioned state, in rows and columns: the screen the
        // viewports are placed on (p. 5-10). Set by the first Create Partition.
        private int _usableRows;
        private int _usableColumns;

        // When an inbound record is an Inbound 3270DS structured field (a partition with a
        // nonzero PID, p. 6-7), the position of its length bytes, filled in by Send; -1 otherwise.
        private int _inboundFieldStart = -1;

        // The current SA set, applied to each character written (p. 4-5).
        private byte _saHighlight;
        private byte _saColor;
        private byte _saCharset;
        private byte _saBackground;
        private byte _saTransparency;

        private byte _lastAid = Aid.None;

        // The character attribute values last sent inbound with SA, in the order of
        // CharacterAttributeTypes. Every inbound record starts with all at default (p. 3-12, 3-13).
        private readonly byte[] _inboundSa = new byte[5];

        private int _fileTransferLimit;

        // Set when the record being applied raised ProtocolError.
        private bool _failed;
        private CodePage _codePage = CodePage.Cp037;

        /// <summary>
        /// Creates a display with a default and an alternate screen size.
        /// </summary>
        /// <remarks>
        /// The default size is used after Erase/Write, the alternate size after Erase/Write
        /// Alternate (GA23-0059-4 p. 3-5). A model 2 has 24 x 80 for both.
        /// </remarks>
        /// <param name="rows">
        /// Default rows.
        /// </param>
        /// <param name="columns">
        /// Default columns.
        /// </param>
        /// <param name="alternateRows">
        /// Alternate rows.
        /// </param>
        /// <param name="alternateColumns">
        /// Alternate columns.
        /// </param>
        public Display3270(int rows = 24, int columns = 80, int alternateRows = 24, int alternateColumns = 80)
        {
            if (rows < 1 || columns < 1 || alternateRows < 1 || alternateColumns < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(rows), "Every dimension must be at least 1.");
            }

            if (rows * columns > 0x3FFF || alternateRows * alternateColumns > 0x3FFF)
            {
                // Above 16383 cells only 16-bit addressing reaches, which needs partitions (p. D-1).
                throw new ArgumentOutOfRangeException(nameof(rows), "More than 16383 cells needs 16-bit addressing, which is not built.");
            }

            _defaultRows = rows;
            _defaultColumns = columns;
            _alternateRows = alternateRows;
            _alternateColumns = alternateColumns;
            _sessionRows = rows;
            _sessionColumns = columns;
            _sessionAlternateRows = alternateRows;
            _sessionAlternateColumns = alternateColumns;

            // Power on: implicit partition state, implicit partition 0 at the default size
            // (p. 2-6).
            _implicitPartition = new Partition3270(0, rows, columns, 0, 0, sixteenBitAddressing: false);
            _p = _implicitPartition;
        }

        /// <summary>
        /// Gets the default size rows Erase/Write uses now: the model's, or a BIND's.
        /// </summary>
        public int DefaultRows => _sessionRows;

        /// <summary>
        /// Gets the default size columns Erase/Write uses now: the model's, or a BIND's.
        /// </summary>
        public int DefaultColumns => _sessionColumns;

        /// <summary>
        /// Gets the alternate size rows Erase/Write Alternate uses now: the model's, or a BIND's.
        /// </summary>
        public int AlternateRows => _sessionAlternateRows;

        /// <summary>
        /// Gets the alternate size columns Erase/Write Alternate uses now: the model's, or a
        /// BIND's.
        /// </summary>
        public int AlternateColumns => _sessionAlternateColumns;

        /// <summary>
        /// Gets a value indicating whether the screen belongs to the SSCP-LU session: the host's
        /// control program talks to the operator in plain text, not the 3270 data stream
        /// (GA23-0061-2 p. 5-36, 5-37; RFC 2355 section 10.5.1).
        /// </summary>
        public bool SscpLuMode { get; private set; }

        /// <summary>
        /// Takes the screen sizes of a BIND for the LU-LU session that has just started, and
        /// clears the screen to the new default size.
        /// </summary>
        /// <remarks>
        /// <para>
        /// GA23-0061-2 Figure 1-11 (p. 1-20, 1-21) gives byte 24's codes; see
        /// <see cref="BindScreenSize"/>. With the base codes and with X'7E' there is one size for
        /// the session and Erase/Write Alternate runs as Erase/Write, so the alternate size is
        /// made the same as the default. Rows and columns are checked against this model the way
        /// the manual's tables do: columns 40, 80 or 132 (or one of the model's own column
        /// counts, for a screen the 1985 manual does not know, such as IBM-DYNAMIC), and no more
        /// rows or columns than the model has. One difference from those tables: a model 1 with
        /// 12 rows of 80 columns is in them, and is refused here because a 12 x 40 model has no
        /// 80th column.
        /// </para>
        /// <para>
        /// A 3274 refuses a bad BIND with sense X'0821' (p. 1-21). Under TN3270E the server has
        /// already accepted it (RFC 2355 section 10.3), so the terminal cannot refuse it; the
        /// sizes are not taken, the model's stay, and <see cref="ProtocolError"/> says why. x3270
        /// does the same (Common\telnet.c, process_bind: "Ignoring invalid BIND image screen size
        /// parameters"). Clearing the screen is what a new size needs (sizes change only at an
        /// erase, GA23-0059-4 p. 3-6); x3270 also erases on every BIND (process_bind,
        /// ctlr_erase).
        /// </para>
        /// </remarks>
        /// <param name="bind">
        /// The BIND image, read by <see cref="BindImage.TryRead"/>.
        /// </param>
        /// <returns>
        /// False when the sizes were refused and <see cref="ProtocolError"/> was raised.
        /// </returns>
        public bool ApplyBind(in BindImage bind)
        {
            SscpLuMode = false;
            NvtMode = false;

            // GA23-0059-4 Appendix C, Table C-1 (p. C-1, read from the page image 24 November
            // 2026), row "Bind Unowned": Partitions R, Inbound Reply Mode R, Selection R,
            // Indicators R, INPID/INOP R, PS Content/PS LCID NC. Row "Bind SSCP" is 9 in every
            // column but INPID/INOP ("the state will already be set", note 9), and R there, so
            // doing the resets is right for both. What RetroIO models of that, done here whether
            // or not the sizes below are taken (the reset is the BIND's, not the sizes'):
            //  - Inbound Reply Mode R: back to field mode. "Reset" is field mode by Table 3-3
            //    (p. 3-4): "reset the inbound reply mode to field".
            //  - INPID/INOP R: INPID 0 and INOP Read Modified, the normal read state, so no
            //    inbound record is kept for a retry any more (p. 3-14: a Read Modified repeats
            //    INOP only in a retry state; p. 3-19: INOP becomes Read Modified when INPID is
            //    set to 0).
            //  - Partitions R: every explicit partition is destroyed ("Any explicitly created
            //    partitions are destroyed by BIND, UNBIND, ...", p. 5-13 note 2) and implicit
            //    partition 0 is made again at the session's size.
            // ResetToImplicit below does all three, in both the taken and the refused branch.
            // Not modelled, so nothing to reset: operator attribute selection and its indicators
            // (Selection, Indicators) - RetroIO has no operator selection. PS Content is NC.

            int rows;
            int columns;
            int alternateRows;
            int alternateColumns;
            bool model1 = _defaultRows == 12 && _defaultColumns == 40;
            string? why = null;
            switch (bind.ScreenSizeCode)
            {
                case BindScreenSize.BaseDefault:
                    // "the device assumes the 3277 default size defined for that model": 12 x 40
                    // on a model 1, 24 x 80 on the others (p. 1-21 table).
                    rows = model1 ? 12 : 24;
                    columns = model1 ? 40 : 80;
                    alternateRows = rows;
                    alternateColumns = columns;
                    break;
                case BindScreenSize.BaseModel1:
                    rows = 12;
                    columns = 40;
                    alternateRows = rows;
                    alternateColumns = columns;
                    if (!model1)
                    {
                        why = "byte 24 X'01' is a model 1 screen of 12 x 40, which a model 2 to 5 rejects with X'0821' (GA23-0061-2 p. 1-21)";
                    }

                    break;
                case BindScreenSize.BaseModel2:
                    rows = 24;
                    columns = 80;
                    alternateRows = rows;
                    alternateColumns = columns;
                    if (model1)
                    {
                        why = "byte 24 X'02' is a 24 x 80 screen, which a model 1 rejects with X'0821' (GA23-0061-2 p. 1-21)";
                    }

                    break;
                case BindScreenSize.DefaultAndQueryAlternate:
                    rows = 24;
                    columns = 80;
                    alternateRows = _alternateRows;
                    alternateColumns = _alternateColumns;
                    break;
                case BindScreenSize.ExtendedDefault:
                    rows = bind.DefaultRows;
                    columns = bind.DefaultColumns;
                    alternateRows = rows;
                    alternateColumns = columns;
                    break;
                case BindScreenSize.ExtendedAlternate:
                    rows = bind.DefaultRows;
                    columns = bind.DefaultColumns;
                    alternateRows = bind.AlternateRows;
                    alternateColumns = bind.AlternateColumns;
                    break;
                default:
                    rows = 0;
                    columns = 0;
                    alternateRows = 0;
                    alternateColumns = 0;
                    why = "byte 24 X'" + bind.ScreenSizeCode.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' is not a screen-size code of GA23-0061-2 Figure 1-11";
                    break;
            }

            if (why == null)
            {
                why = CheckBindSize("default", rows, columns) ?? CheckBindSize("alternate", alternateRows, alternateColumns);
            }

            if (why != null)
            {
                // The model's sizes stay; the screen is still cleared, as for any BIND.
                _sessionRows = _defaultRows;
                _sessionColumns = _defaultColumns;
                _sessionAlternateRows = _alternateRows;
                _sessionAlternateColumns = _alternateColumns;
                ResetToImplicit(_sessionRows, _sessionColumns);
                Error("The BIND's screen sizes were not taken, the model's are kept: " + why + ".");
                return false;
            }

            _sessionRows = rows;
            _sessionColumns = columns;
            _sessionAlternateRows = alternateRows;
            _sessionAlternateColumns = alternateColumns;
            ResetToImplicit(rows, columns);
            return true;
        }

        /// <summary>
        /// The LU-LU session has ended (an UNBIND): the screen goes back to the model's default
        /// size and is cleared.
        /// </summary>
        /// <remarks>
        /// GA23-0061-2 p. 1-20: operation stays in alternate mode until, among others, "the SNA
        /// session is unbound"; "Only these conditions return the display ... to the
        /// default-value screen-size". With the session gone, its BIND sizes go too. x3270 does
        /// the same (Common\telnet.c, process_eor, TN3270E_DT_UNBIND: the model sizes back, then
        /// ctlr_erase).
        /// </remarks>
        public void Unbind()
        {
            // GA23-0059-4 Appendix C, Table C-1 (p. C-1, read from the page image 24 November
            // 2026), row "Unbind LULU (8)": Partitions 17, Inbound Reply Mode 13, Selection NC,
            // Indicators 7, INPID/INOP NR, PS Content/PS LCID NC. Note 13 (p. C-2): "Change the
            // inbound reply mode to allow all selections." That is NOT done here, on purpose:
            // the manual defines field, extended field and character mode (p. 5-61, 5-62) and
            // nothing called "allow all selections"; the selections it speaks of are operator
            // attribute selections, which RetroIO does not have. Which reply mode that means is
            // not known, so the mode is left as it was rather than guessed (BUGS.md, Q group,
            // "Tn3270 Unbind reply mode"). The next BIND resets it to field mode (ApplyBind).
            // Note 17 is printed for Partitions but p. C-2 has no note 17; p. 5-13 note 2 says
            // "Any explicitly created partitions are destroyed by BIND, UNBIND, ...", so they are,
            // and implicit partition 0 is made again at the model's size below, keeping the reply
            // mode it had (note 13 above). INPID/INOP NR: a later action sets them; only when an
            // explicit partition is destroyed does INPID go to 0 with it (p. 3-20). Note 8 (the last write stays on the screen while unowned) is not
            // what this method does: it clears the screen as x3270 does (see remarks); that
            // difference is in the same BUGS.md entry.
            SscpLuMode = false;
            NvtMode = false;
            _sessionRows = _defaultRows;
            _sessionColumns = _defaultColumns;
            _sessionAlternateRows = _alternateRows;
            _sessionAlternateColumns = _alternateColumns;
            if (_explicitState)
            {
                ResetToImplicit(_sessionRows, _sessionColumns, resetReplyMode: false);
                return;
            }

            Resize(_sessionRows, _sessionColumns);
            CursorAddress = 0;
        }

        /// <summary>
        /// Shows one message from the SSCP on the SSCP-LU session, and enters SSCP-LU mode if the
        /// screen was not in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// GA23-0061-2 p. 5-37, "Outbound Message Handling": the message is SCS text. The only
        /// control codes are NL (X'15') and, with the APL feature, Graphic Escape (X'08'); every
        /// other byte is a graphic. It is shown at the cursor; NL fills the rest of the line with
        /// nulls and moves to the start of the next line; past the last line the message wraps
        /// to the top. The cursor is left after the last character (at the start of the next
        /// line after an ending NL), and that address is "the initial cursor address", where the
        /// operator's message starts. There is "an outbound limit of 1,536 bytes of data".
        /// </para>
        /// <para>
        /// Entering SSCP-LU mode clears the screen and sets "the maximum physical size"
        /// (p. 5-36, "Setting the Screen Size": from the unowned state to SSCP ownership; "The
        /// screen also is cleared by the transfer of ownership from unowned to SSCP-owned when
        /// this state transfer is caused by an outbound RU from the SSCP"; "Pressing the SYS REQ
        /// key causes the screen to be cleared"). x3270 does not clear (Common\ctlr.c,
        /// ctlr_write_sscp_lu writes over what is there); the manual is followed.
        /// </para>
        /// <para>
        /// The keyboard is unlocked afterwards: "It is unlocked when in contention or
        /// send-not-xmit", and receiving a request and answering it puts the LU in contention
        /// (p. 5-36).
        /// </para>
        /// </remarks>
        /// <param name="data">
        /// The data part of a TN3270E SSCP-LU-DATA message.
        /// </param>
        /// <returns>
        /// False when the message was refused and <see cref="ProtocolError"/> was raised.
        /// </returns>
        public bool ProcessSscpLuData(ReadOnlySpan<byte> data)
        {
            _failed = false;
            if (data.Length > MaxSscpOutbound)
            {
                Error("An SSCP-LU message of " + data.Length + " bytes is over the 3274's outbound limit of 1,536 bytes (GA23-0061-2 p. 5-37); it was not shown.");
                return false;
            }

            if (!SscpLuMode)
            {
                SscpLuMode = true;
                NvtMode = false;
                bool alternateIsLarger = _alternateRows * _alternateColumns > _defaultRows * _defaultColumns;
                int sscpRows = alternateIsLarger ? _alternateRows : _defaultRows;
                int sscpColumns = alternateIsLarger ? _alternateColumns : _defaultColumns;
                if (_explicitState)
                {
                    // The SSCP's text fills the whole screen, which explicit partitions cannot
                    // share, so they are destroyed. GA23-0059-4 does not list an SSCP message
                    // among what destroys them (p. 5-13 note 2 names BIND, UNBIND, ACTLU, DACTLU,
                    // ACTPU and DACTPU); this is a reading, not a quoted rule. The reply mode is
                    // left as it was, as it always was for this switch.
                    ResetToImplicit(sscpRows, sscpColumns, resetReplyMode: false);
                }
                else
                {
                    Resize(sscpRows, sscpColumns);
                    CursorAddress = 0;
                }

                InsertMode = false;
            }

            int size = Size;
            int columns = Columns;
            int a = CursorAddress;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                if (b == ScsNewLine)
                {
                    // Nulls to the end of this line, then the first position of the next one.
                    int nextRow = ((a / columns) + 1) * columns;
                    for (int n = a; n < nextRow; n++)
                    {
                        StoreCharacter(n, FormatControl.Null, graphicEscape: false, useSa: false);
                        ClearCharacterAttributes(n);
                    }

                    a = nextRow == size ? 0 : nextRow;
                    continue;
                }

                bool graphicEscape = false;
                if (b == Order3270.GraphicEscape && i + 1 < data.Length)
                {
                    // "when the APL/Text feature is installed, the Graphic Escape character":
                    // the next byte is from the APL set (code page 310), as GE is in the 3270
                    // data stream (GA23-0059-4 p. 4-21).
                    i++;
                    b = data[i];
                    graphicEscape = true;
                }

                StoreCharacter(a, b, graphicEscape, useSa: false);
                ClearCharacterAttributes(a);
                a = a + 1 == size ? 0 : a + 1;
            }

            CursorAddress = a;
            _sscpStart = a;
            ClearAllInputInhibit();
            return true;
        }

        /// <summary>
        /// The screen goes back to the LU-LU session (3270 data from the host application again);
        /// what is on it stays until the host writes.
        /// </summary>
        public void LeaveSscpLuMode() => SscpLuMode = false;

        /// <summary>
        /// Gets a value indicating whether the screen is in TN3270E NVT mode: the host sends plain
        /// ASCII text, not the 3270 data stream (RFC 2355 section 9.1).
        /// </summary>
        public bool NvtMode { get; private set; }

        /// <summary>
        /// Shows one TN3270E NVT-DATA message, and enters NVT mode if the screen was not in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// RFC 2355 section 9.1: "Switching modes results in the client performing the equivalent
        /// of a 3270 Erase/Reset operation ... using the default partition (screen) size", so
        /// entering NVT mode clears the screen to the default size. The data is NVT text (RFC
        /// 854, "The Network Virtual Terminal"): printable ASCII is shown through the code page,
        /// CR goes to the start of the line, LF to the next line (the top after the last), BS one
        /// position left, HT to the next multiple of 8, BEL sounds the alarm. NUL is "No
        /// Operation". Any other control byte (ESC and the sequences it starts, among others) is
        /// not shown, and <see cref="ProtocolError"/> says which: an ANSI terminal is not built
        /// into this 3270 screen. A character the code page has no EBCDIC byte for is not shown
        /// either, and said the same way.
        /// </para>
        /// <para>
        /// The keyboard is left unlocked: NVT mode has no keyboard lock of its own in RFC 2355 or
        /// RFC 854. The operator's text starts where the host's output ended, as on the SSCP-LU
        /// screen (<see cref="Attention"/>).
        /// </para>
        /// </remarks>
        /// <param name="data">
        /// The data part of the NVT-DATA message, telnet framing already removed.
        /// </param>
        /// <returns>
        /// False when part of the text was not shown and <see cref="ProtocolError"/> was raised.
        /// </returns>
        public bool ProcessNvtData(ReadOnlySpan<byte> data)
        {
            _failed = false;
            if (!NvtMode)
            {
                EnterNvtMode();
            }

            int size = Size;
            int columns = Columns;
            int a = CursorAddress;
            int firstRefused = -1;
            for (int i = 0; i < data.Length; i++)
            {
                byte b = data[i];
                switch (b)
                {
                    case 0x00: // NUL, "No Operation" (RFC 854)
                        break;
                    case 0x07: // BEL
                        Alarm?.Invoke();
                        break;
                    case 0x08: // BS
                        a = a == 0 ? 0 : a - 1;
                        break;
                    case 0x09: // HT: the next multiple of 8 on this line, at most its last column
                    {
                        int column = a % columns;
                        int next = Math.Min(columns - 1, ((column / 8) + 1) * 8);
                        a = a - column + next;
                        break;
                    }

                    case 0x0A: // LF: the same column, one line down
                        a += columns;
                        if (a >= size)
                        {
                            a -= size;
                        }

                        break;
                    case 0x0D: // CR: the start of this line
                        a -= a % columns;
                        break;
                    default:
                        if (b < 0x20 || b > 0x7E || !_codePage.TryFromUnicode((char)b, out byte ebcdic))
                        {
                            if (firstRefused < 0)
                            {
                                firstRefused = b;
                            }

                            break;
                        }

                        StoreCharacter(a, ebcdic, graphicEscape: false, useSa: false);
                        ClearCharacterAttributes(a);
                        a = a + 1 == size ? 0 : a + 1;
                        break;
                }
            }

            CursorAddress = a;
            _sscpStart = a;
            ClearAllInputInhibit();
            if (firstRefused >= 0)
            {
                Error("NVT text byte X'" + firstRefused.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' was not shown: only printable ASCII, CR, LF, BS, HT and BEL are built for NVT mode (RFC 854); an ANSI terminal is not built into the 3270 screen.");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Leaves NVT mode for 3270 mode: the screen is cleared to the default size, as RFC 2355
        /// section 9.1 says for every mode switch. Does nothing when not in NVT mode.
        /// </summary>
        public void LeaveNvtMode()
        {
            if (!NvtMode)
            {
                return;
            }

            NvtMode = false;
            Resize(_sessionRows, _sessionColumns);
            CursorAddress = 0;
        }

        // RFC 2355 9.1: Erase/Reset at the default size, then plain text.
        private void EnterNvtMode()
        {
            NvtMode = true;
            SscpLuMode = false;
            if (_explicitState)
            {
                // Erase/Reset destroys every partition (GA23-0059-4 p. 5-15). The reply mode is
                // left as this switch always left it.
                ResetToImplicit(_sessionRows, _sessionColumns, resetReplyMode: false);
            }
            else
            {
                Resize(_sessionRows, _sessionColumns);
                CursorAddress = 0;
            }

            _sscpStart = 0;
            InsertMode = false;
        }

        // Enter in NVT mode: the operator's text from where the host's output ended, nulls left
        // out, as ASCII, then CR LF, the NVT end of line (RFC 854). RFC 2355 9.1: the client
        // "should buffer data until an entire message is built (for the client, this would
        // normally mean until the user presses Enter)". The cursor goes to the start of the next
        // line, where the next message starts. Clear clears the screen locally. Every other
        // attention key is Minus Function: RFC 2355 gives PA and PF keys no NVT meaning (x3270
        // sends them as escape sequences, Common\kybd.c nvt_send_pf, which no RFC names).
        private bool NvtAttention(byte aid)
        {
            if (aid == Aid.Clear)
            {
                Resize(Rows, Columns);
                CursorAddress = 0;
                _sscpStart = 0;
                InsertMode = false;
                return true;
            }

            if (aid != Aid.Enter)
            {
                _deviceLock = KeyboardLock.MinusFunction;
                return false;
            }

            InsertMode = false;
            _inbound.Clear();
            int size = Size;
            for (int a = _sscpStart; a < size; a++)
            {
                byte b = _p.Buffer[a];
                if (b == FormatControl.Null || _p.IsFieldAttributeAt[a] || _p.GraphicEscapeAt[a])
                {
                    continue;
                }

                char c = _codePage.ToUnicode(b);
                if (c >= 0x20 && c <= 0x7E)
                {
                    AppendByte((byte)c);
                }
            }

            AppendByte(0x0D);
            AppendByte(0x0A);
            int row = CursorAddress / Columns;
            int next = (row + 1) * Columns;
            CursorAddress = next >= size ? 0 : next;
            _sscpStart = CursorAddress;
            Send();
            return true;
        }

        // An attention key in SSCP-LU mode (GA23-0061-2 p. 5-37, "Inbound Message Handling"):
        //  - ENTER sends "the data (excluding nulls) contained in the first 256 screen character
        //    positions including and following the cursor address, or to the end of screen,
        //    whichever occurs first", from the initial cursor address. The manual names no AID
        //    and no cursor address in front of it, and x3270 sends neither (Common\ctlr.c,
        //    ctlr_read_modified: "if (!IN_SSCP)" around the AID and cursor). The keyboard locks
        //    until the SSCP answers ("locked when in receive or send-xmit", p. 5-36).
        //  - CLEAR clears the screen and resets the initial cursor address; nothing is sent.
        //  - any other PA or PF key: "Input Inhibited and Minus Function symbols are turned on",
        //    nothing is sent.
        private bool SscpAttention(byte aid)
        {
            if (aid == Aid.Clear)
            {
                Resize(Rows, Columns);
                CursorAddress = 0;
                _sscpStart = 0;
                InsertMode = false;
                return true;
            }

            if (aid != Aid.Enter)
            {
                _deviceLock = KeyboardLock.MinusFunction;
                return false;
            }

            InsertMode = false;
            _inbound.Clear();
            Array.Clear(_inboundSa);
            int end = Math.Min(Size, _sscpStart + MaxSscpInbound);
            for (int a = _sscpStart; a < end; a++)
            {
                byte b = _p.Buffer[a];
                if (b == FormatControl.Null || _p.IsFieldAttributeAt[a])
                {
                    continue;
                }

                if (_p.GraphicEscapeAt[a])
                {
                    // Not in the manual's inbound paragraph; GE before an APL character is how
                    // the outbound side marks one (p. 5-37), and x3270 sends it the same way.
                    AppendByte(Order3270.GraphicEscape);
                }

                AppendByte(b);
            }

            _lastAid = aid;

            // An enter action on the implicit partition: TWAIT and System Lock, retry-enter state
            // (GA23-0059-4 p. 3-21 steps 5 to 9). Nothing is kept for a retry: the SSCP-LU
            // session has no read commands.
            _inboundPid = 0;
            _inop = Command3270.ReadModified;
            _p.Pwait = true;
            _p.SysLock = true;
            _readState = ReadState.RetryEnter;
            _retryLength = 0;
            Send();
            return true;
        }

        // Checks one BIND screen size against this model (GA23-0061-2 p. 1-21 tables); null when
        // it is good, otherwise why not.
        private string? CheckBindSize(string which, int rows, int columns)
        {
            int maxRows = Math.Max(_defaultRows, _alternateRows);
            int maxColumns = Math.Max(_defaultColumns, _alternateColumns);
            bool knownColumns = columns == 40 || columns == 80 || columns == 132 || columns == _defaultColumns || columns == _alternateColumns;
            if (rows < 1 || rows > maxRows)
            {
                return "the " + which + " size has " + rows + " rows; this model has 1 to " + maxRows + " (GA23-0061-2 p. 1-21)";
            }

            if (!knownColumns || columns > maxColumns)
            {
                return "the " + which + " size has " + columns + " columns; this model takes 40, 80 or 132 up to " + maxColumns + " (GA23-0061-2 p. 1-21)";
            }

            if (rows * columns > Math.Max(_defaultRows * _defaultColumns, _alternateRows * _alternateColumns))
            {
                return "the " + which + " size " + rows + " x " + columns + " holds more cells than this model's largest screen";
            }

            return null;
        }

        /// <summary>
        /// A record the terminal sends to the host: an AID reply, a read reply or query replies.
        /// </summary>
        /// <remarks>
        /// The span is only valid during the call.
        /// </remarks>
        public event InboundRecordHandler? InboundRecord;

        /// <summary>
        /// The host sent something this model rejects or has not built.
        /// </summary>
        /// <remarks>
        /// The record is not applied past that point. The text says what and why, with the page of
        /// the manual that decides it.
        /// </remarks>
        public event ProtocolErrorHandler? ProtocolError;

        /// <summary>
        /// The WCC asked to sound the alarm (GA23-0059-4 p. 3-3).
        /// </summary>
        public event AlarmHandler? Alarm;

        /// <summary>
        /// A file transfer structured field (ID X'D0') arrived in a Write Structured Field.
        /// </summary>
        /// <remarks>
        /// Raised only while <see cref="FileTransferLimit"/> is above 0; otherwise such a field is a
        /// <see cref="ProtocolError"/>. The span is the whole structured field, length bytes first, and
        /// is only valid during the call. <see cref="IndFileTransfer"/> handles it.
        /// </remarks>
        public event StructuredFieldHandler? FileTransferField;

        /// <summary>
        /// Gets the number of rows of the active partition's presentation space. In implicit
        /// partition state that is the screen.
        /// </summary>
        public int Rows => _p.Rows;

        /// <summary>
        /// Gets the number of columns of the active partition's presentation space. In implicit
        /// partition state that is the screen.
        /// </summary>
        public int Columns => _p.Columns;

        /// <summary>
        /// Gets rows times columns of the active partition.
        /// </summary>
        public int Size => Rows * Columns;

        /// <summary>
        /// Gets the cursor's buffer address in the active partition (GA23-0059-4 p. 2-5: the screen
        /// cursor is displayed at the current cursor position of the active partition).
        /// </summary>
        public int CursorAddress
        {
            get => _p.Cursor;
            private set
            {
                _p.Cursor = value;

                // The cursor never leaves the window (p. 2-5, 7-16): a new cursor position outside
                // it is an automatic scroll (p. 7-17), which also puts the window at (0, 0) when
                // Erase/Write puts the cursor at 0 (p. 3-6 step 5).
                ScrollToCursor(_p);
            }
        }

        /// <summary>
        /// Gets a value indicating whether the display is in explicit partitioned state: the host
        /// has created partitions with Create Partition (GA23-0059-4 p. 2-7). False means implicit
        /// partition state, one implicit partition 0 that is the whole screen.
        /// </summary>
        public bool ExplicitPartitioned => _explicitState;

        /// <summary>
        /// Gets the PID of the active partition, the one the operator's keys and the cursor are in
        /// (p. 2-5). 0 in implicit partition state.
        /// </summary>
        public byte ActivePartitionId => _activePid;

        /// <summary>
        /// Gets INPID, the partition inbound data comes from and that a retry repeats (p. 3-20).
        /// </summary>
        public byte InboundPartitionId => _inboundPid;

        /// <summary>
        /// Gets the rows of the screen the partitions' viewports are placed on: in implicit
        /// partition state the implicit partition's rows, in explicit partitioned state the usable
        /// area's (p. 2-7, 5-10).
        /// </summary>
        public int ScreenRows => _explicitState ? _usableRows : _implicitPartition.Rows;

        /// <summary>
        /// Gets the columns of the screen the partitions' viewports are placed on.
        /// </summary>
        public int ScreenColumns => _explicitState ? _usableColumns : _implicitPartition.Columns;

        /// <summary>
        /// Gets the active partition: its presentation space, cursor and viewport.
        /// </summary>
        public Partition3270 ActivePartition => PartitionFor(_activePid) ?? _implicitPartition;

        /// <summary>
        /// The partition with a PID, or null when there is none. In implicit partition state only
        /// PID 0 exists, the implicit partition.
        /// </summary>
        /// <param name="pid">
        /// The partition identifier.
        /// </param>
        /// <returns>
        /// The partition, or null.
        /// </returns>
        public Partition3270? GetPartition(byte pid) => PartitionFor(pid);

        /// <summary>
        /// The partition whose viewport covers a screen position, and the buffer address of that
        /// position in it.
        /// </summary>
        /// <remarks>
        /// The viewport shows the window, the part of the presentation space from row
        /// <see cref="Partition3270.WindowRow"/> on, "in the corresponding row and column of the
        /// viewport" (GA23-0059-4 p. 2-3), so the address is (RW + row - RV) x W + (column - CV).
        /// Without scrolling RW is 0 and the window is the whole presentation space. A position
        /// outside every viewport shows nothing (p. 5-14: a destroyed partition's viewport is
        /// cleared). This is what a renderer draws at that screen position.
        /// </remarks>
        /// <param name="row">
        /// The screen row, 0 at the top of the usable area.
        /// </param>
        /// <param name="column">
        /// The screen column.
        /// </param>
        /// <param name="partition">
        /// The partition, or null.
        /// </param>
        /// <param name="address">
        /// The buffer address in that partition, or -1.
        /// </param>
        /// <returns>
        /// True when a viewport covers the position.
        /// </returns>
        public bool TryGetScreenPosition(int row, int column, out Partition3270? partition, out int address)
        {
            if (!_explicitState)
            {
                Partition3270 ip = _implicitPartition;
                if (row >= 0 && row < ip.Rows && column >= 0 && column < ip.Columns)
                {
                    partition = ip;
                    address = (row * ip.Columns) + column;
                    return true;
                }
            }
            else
            {
                for (int i = 0; i < _partitions.Length; i++)
                {
                    Partition3270? p = _partitions[i];
                    if (p != null
                        && row >= p.ViewportRow && row < p.ViewportRow + p.ViewportRows
                        && column >= p.ViewportColumn && column < p.ViewportColumn + p.ViewportColumns)
                    {
                        partition = p;
                        address = ((p.WindowRow + row - p.ViewportRow) * p.Columns) + (column - p.ViewportColumn);
                        return true;
                    }
                }
            }

            partition = null;
            address = -1;
            return false;
        }

        /// <summary>
        /// Gets or sets where the vertical outline is drawn across a character cell, in the cell
        /// units of the Usable Area reply (VPOS of the Field Outlining query reply, GA23-0059-4
        /// p. 6-49, 6-50), counted from the cell's left edge.
        /// </summary>
        /// <remarks>
        /// The renderer draws the outlines and should set this to where it draws them. The
        /// default 0, the cell's left edge, is an ASSUMPTION, not a measured value.
        /// </remarks>
        public int OutlineVerticalPosition { get; set; }

        /// <summary>
        /// Gets or sets where the overline and underline are drawn down a character cell, in the
        /// cell units of the Usable Area reply (HPOS of the Field Outlining query reply, p. 6-49,
        /// 6-50), counted from the cell's top edge.
        /// </summary>
        /// <remarks>
        /// p. 6-49: "When the overline is drawn above the cell, the location of the underline,
        /// which is the same as the location of the overline in the next row, is used." The
        /// default 15, the bottom pel row of the default 16-high cell, is an ASSUMPTION, not a
        /// measured value; the renderer should set it.
        /// </remarks>
        public int OutlineHorizontalPosition { get; set; } = 15;

        /// <summary>
        /// Gets why the keyboard is locked for the active partition, or
        /// <see cref="KeyboardLock.None"/>.
        /// </summary>
        /// <remarks>
        /// The conditions of the display come first (operator errors, Minus Function, the query
        /// TWAIT, the trigger conditions), because Activate Partition leaves their indicators up
        /// (GA23-0059-4 p. 5-8 step 5b). Then the active partition's own: PWAIT, shown as TWAIT
        /// (<see cref="KeyboardLock.WaitingForHost"/>) when there is only one partition (p. 3-20),
        /// then System Lock, which "will not be displayed while TWAIT or PWAIT exists" (p. 3-21).
        /// The Enter Inhibit condition has no indicator (p. 3-21); see <see cref="EnterInhibit"/>.
        /// </remarks>
        public KeyboardLock Lock
        {
            get
            {
                if (_deviceLock != KeyboardLock.None)
                {
                    return _deviceLock;
                }

                Partition3270 active = ActivePartition;
                if (active.Pwait)
                {
                    return _explicitState && CountPartitions() > 1 ? KeyboardLock.PartitionWait : KeyboardLock.WaitingForHost;
                }

                return active.SysLock ? KeyboardLock.SystemLock : KeyboardLock.None;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the display has the Enter Inhibit condition: an inbound
        /// operation (an enter action, a trigger action, a Read Partition) has not been
        /// acknowledged, so no AID can be sent from any partition, while typing into a partition
        /// with no other condition is still allowed (GA23-0059-4 p. 3-15, 3-21).
        /// </summary>
        public bool EnterInhibit => _readState != ReadState.Normal;

        /// <summary>
        /// Gets a value indicating whether a trigger field has been sent to the host and the
        /// display waits for its affirmative or negative reply (GA23-0059-4 p. 4-23). Keystrokes
        /// meanwhile are queued, not shown.
        /// </summary>
        public bool TriggerPending => _triggerPending;

        /// <summary>
        /// Gets how many keystrokes are queued while a trigger field is validated (p. 4-23).
        /// </summary>
        public int QueuedKeystrokes => _queueCount;

        /// <summary>
        /// Gets a value indicating whether the keyboard is in insert mode.
        /// </summary>
        /// <remarks>
        /// Switched by the Insert key through <see cref="ToggleInsert"/>; ended by Reset and by every
        /// key that talks to the host (GA23-0061-2 p. C-5, C-6).
        /// </remarks>
        public bool InsertMode { get; private set; }

        /// <summary>
        /// Gets or sets a value indicating whether the screen shows colour, as a 3279 does.
        /// </summary>
        /// <remarks>
        /// Only changes the Color query reply (GA23-0059-4 p. 6-29, 6-30): with colour every value
        /// X'F1'-X'F7' is shown as itself; without, every value is shown as the default colour.
        /// The colour attributes are stored either way. Default true.
        /// </remarks>
        public bool Color { get; set; } = true;

        /// <summary>
        /// Gets or sets the width of one character cell, in screen pixels, as the Usable Area and
        /// Character Sets query replies report it (AW and SDW, p. 6-87, 6-22).
        /// </summary>
        /// <remarks>
        /// The renderer knows its font and should set this. The default of 9 is an ASSUMPTION (the
        /// usual 3278 cell width), not a measured value.
        /// </remarks>
        public int CellWidth { get; set; } = 9;

        /// <summary>
        /// Gets or sets the height of one character cell, in screen pixels (AH and SDH, p. 6-87, 6-22).
        /// </summary>
        /// <remarks>
        /// The renderer should set this. The default of 16 is an ASSUMPTION, not a measured value.
        /// </remarks>
        public int CellHeight { get; set; } = 16;

        /// <summary>
        /// Gets or sets the distance between two dots across, in millimetres: the 2-byte numerator
        /// in the high half, the 2-byte denominator in the low half (Xr of the Usable Area query
        /// reply, UNITS X'01', p. 6-87, 6-89).
        /// </summary>
        /// <remarks>
        /// A display with Load PS may not send X'FFFFFFFF', "cannot be determined" (p. 6-89), so a
        /// real value is needed. The default X'000A02E5' (10/741 mm) is x3270's canned 3279 model 2
        /// value (`Xr_3279_2` in its sf.c), an ASSUMPTION about the renderer, not a measurement.
        /// </remarks>
        public uint DotDistanceX { get; set; } = 0x000A02E5;

        /// <summary>
        /// Gets or sets the distance between two dots down, in millimetres, as for
        /// <see cref="DotDistanceX"/> (Yr, p. 6-87, 6-89).
        /// </summary>
        /// <remarks>
        /// The default X'0002006F' (2/111 mm) is x3270's `Yr_3279_2`, an ASSUMPTION.
        /// </remarks>
        public uint DotDistanceY { get; set; } = 0x0002006F;

        /// <summary>
        /// Gets the loadable character sets the host fills with Load Programmed Symbols
        /// (GA23-0059-4 p. 5-18 to 5-33). A renderer looks up the set by the LCID a field or
        /// character carries (<see cref="GetFieldCharacterSet"/>,
        /// <see cref="GetCharacterCharacterSet"/>) and draws the dots of the code in that slot.
        /// </summary>
        /// <remarks>
        /// Six sets, RWS X'02' to X'07', one per PS key A to F (p. 6-25 names the "PS [A-F]
        /// selection keys"); how many is this display's choice. They survive every reset but power
        /// on (Table C-1 p. C-1, PS Content and PS LCID), so only a new display starts them empty.
        /// </remarks>
        public ProgrammedSymbolStore ProgrammedSymbols { get; } = new ProgrammedSymbolStore(6);

        /// <summary>
        /// Gets or sets the largest file transfer structured field transmission, in bytes, both
        /// ways; 0 switches file transfer off.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Above 0, the Query Reply includes DDM (X'95') with this value as LIMIN and LIMOUT
        /// (GA23-0059-4 p. 6-37), which is what makes IND$FILE choose DFT mode, and X'D0' fields go
        /// to <see cref="FileTransferField"/>. A WSF whose X'D0' fields add up to more than this is
        /// rejected (p. 6-37).
        /// </para>
        /// <para>
        /// 0 is never sent as a limit, because to the host it means "no limit" (p. 6-37).
        /// </para>
        /// </remarks>
        public int FileTransferLimit
        {
            get => _fileTransferLimit;
            set
            {
                if (value < 0 || value > 0x7FFF)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, "The file transfer limit is 0 (off) or 1 to 32767 bytes.");
                }

                _fileTransferLimit = value;
            }
        }

        /// <summary>
        /// Gets the inbound reply mode the host last set with Set Reply Mode.
        /// </summary>
        public ReplyMode ReplyMode => _p.InboundReplyMode;

        /// <summary>
        /// Gets or sets the base code page that turns typed characters into bytes, and that the
        /// Character Sets query reply names. Default 037.
        /// </summary>
        /// <remarks>
        /// Set it before connecting: the host learns the page from the query reply, and bytes
        /// already in the buffer stay as they are. Only base pages are allowed, never
        /// <see cref="CodePage.Cp310"/>, which is the Graphic Escape set beside every base page.
        /// </remarks>
        /// <exception cref="ArgumentException">
        /// Thrown when the value is <see cref="CodePage.Cp310"/>.
        /// </exception>
        public CodePage CodePage
        {
            get => _codePage;
            set
            {
                ArgumentNullException.ThrowIfNull(value);
                if (value == CodePage.Cp310)
                {
                    throw new ArgumentException("Code page 310 is the Graphic Escape set, not a base page.", nameof(value));
                }

                _codePage = value;
            }
        }

        /// <summary>
        /// Gets a value indicating whether the screen has at least one field (a "formatted" screen).
        /// </summary>
        public bool IsFormatted
        {
            get
            {
                for (int i = 0; i < _p.IsFieldAttributeAt.Length; i++)
                {
                    if (_p.IsFieldAttributeAt[i])
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// The byte stored at an address: a character (EBCDIC), a stored control, or - when
        /// <see cref="IsFieldAttribute"/> is true - the field attribute.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The byte.
        /// </returns>
        public byte GetByte(int address) => _p.Buffer[address];

        /// <summary>
        /// True when the position holds a field attribute (shown as a blank, p. 4-13).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// True for a field attribute position.
        /// </returns>
        public bool IsFieldAttribute(int address) => _p.IsFieldAttributeAt[address];

        /// <summary>
        /// True when the character came in with Graphic Escape, so it is from the alternate set
        /// (code page 310) and not the base code page (p. 4-9).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// True for a GE character.
        /// </returns>
        public bool IsGraphicEscape(int address) => _p.GraphicEscapeAt[address];

        /// <summary>
        /// The address of the field attribute that governs a position, or -1 on an unformatted
        /// screen. Searches backwards and wraps, because a field may run past the end of the buffer.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The field attribute's address, or -1.
        /// </returns>
        public int GetFieldAttributeFor(int address)
        {
            int size = Size;
            int a = address;
            for (int n = 0; n < size; n++)
            {
                if (_p.IsFieldAttributeAt[a])
                {
                    return a;
                }

                a = a == 0 ? size - 1 : a - 1;
            }

            return -1;
        }

        /// <summary>
        /// The extended foreground colour of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The colour value (GA23-0061-2 p. 2-10 lists X'F1'-X'F7').
        /// </returns>
        public byte GetFieldColor(int fieldAttributeAddress) => _p.FieldColor[fieldAttributeAddress];

        /// <summary>
        /// The foreground colour character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The colour value.
        /// </returns>
        public byte GetCharacterColor(int address) => _p.CharColor[address];

        /// <summary>
        /// The extended highlighting of the field at a field attribute address.
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The highlight value (p. 4-18).
        /// </returns>
        public byte GetFieldHighlight(int fieldAttributeAddress) => _p.FieldHighlight[fieldAttributeAddress];

        /// <summary>
        /// The highlighting character attribute of one position.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The highlight value.
        /// </returns>
        public byte GetCharacterHighlight(int address) => _p.CharHighlight[address];

        /// <summary>
        /// The character set of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <remarks>
        /// X'F1' is the Graphic Escape set, code page 310: the Character Sets query reply gives it
        /// local ID X'F1' (p. 6-25).
        /// </remarks>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The character set's local ID (p. 4-19).
        /// </returns>
        public byte GetFieldCharacterSet(int fieldAttributeAddress) => _p.FieldCharset[fieldAttributeAddress];

        /// <summary>
        /// The character set character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The character set's local ID.
        /// </returns>
        public byte GetCharacterCharacterSet(int address) => _p.CharCharset[address];

        /// <summary>
        /// The background colour of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The colour value (p. 4-18).
        /// </returns>
        public byte GetFieldBackground(int fieldAttributeAddress) => _p.FieldBackground[fieldAttributeAddress];

        /// <summary>
        /// The background colour character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The colour value.
        /// </returns>
        public byte GetCharacterBackground(int address) => _p.CharBackground[address];

        /// <summary>
        /// The transparency of the field at a field attribute address (X'00' = default).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The transparency value (p. 4-20).
        /// </returns>
        public byte GetFieldTransparency(int fieldAttributeAddress) => _p.FieldTransparency[fieldAttributeAddress];

        /// <summary>
        /// The transparency character attribute of one position (X'00' = take the field's).
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        /// <returns>
        /// The transparency value.
        /// </returns>
        public byte GetCharacterTransparency(int address) => _p.CharTransparency[address];

        /// <summary>
        /// The field validation attribute of the field at a field attribute address (p. 4-21).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The validation bits: see <see cref="FieldValidation"/>.
        /// </returns>
        public byte GetFieldValidation(int fieldAttributeAddress) => _p.FieldValidationAt[fieldAttributeAddress];

        /// <summary>
        /// The field outlining attribute of the field at a field attribute address (p. 4-20).
        /// </summary>
        /// <param name="fieldAttributeAddress">
        /// The field attribute's address.
        /// </param>
        /// <returns>
        /// The outlining bits: see <see cref="FieldOutlining"/>.
        /// </returns>
        public byte GetFieldOutlining(int fieldAttributeAddress) => _p.FieldOutliningAt[fieldAttributeAddress];

        /// <summary>
        /// Applies one complete outbound record (telnet framing already removed).
        /// </summary>
        /// <param name="record">
        /// The record: command byte first.
        /// </param>
        /// <returns>
        /// False when <see cref="ProtocolError"/> was raised for this record. A TN3270E session
        /// that was asked for a response answers negatively then (RFC 2355 section 10.4.1).
        /// </returns>
        public bool ProcessOutbound(ReadOnlySpan<byte> record)
        {
            _failed = false;
            if (record.Length == 0)
            {
                return true;
            }

            byte command = record[0];
            bool isRead = command == Command3270.ReadModified || command == Command3270.ReadModifiedAll || command == Command3270.ReadBuffer;

            // What this record is to a trigger field the host is validating (p. 4-24), decided
            // from the record as it came, before anything in it is applied.
            _triggerReply = _triggerPending ? ClassifyTriggerReply(record) : TriggerReply.None;

            // A read command in a retry state works on the inbound partition (p. 3-22 step 3):
            // Read Modified always ("a retry of the inbound operation on INPID", p. 3-17), Read
            // Buffer and Read Modified All when INOP is a read (3d). With INOP Query they read
            // partition 0 like any read command (3c).
            bool retryRead = false;
            if (isRead && _readState != ReadState.Normal && (command == Command3270.ReadModified || _inop != InopQuery))
            {
                Partition3270? inbound = PartitionFor(_inboundPid);
                if (inbound != null)
                {
                    _p = inbound;
                    retryRead = true;
                }
            }

            if (_explicitState && command != Command3270.WriteStructuredField && !retryRead)
            {
                // A command sent as the first byte, in explicit partitioned state (GA23-0059-4
                // Table 3-3, p. 3-4):
                //  - EW or EWA with WCC reset: "Reset the display to the implicit partition state;
                //    execute the command" - every partition destroyed (p. 3-6 step 6b), then the
                //    command runs as in implicit partition state, which sets the size.
                //  - EW or EWA without reset, and Write: "Execute the command against explicit
                //    partition 0; if no explicit partition 0, reject the command."
                //  - EAU: "implicit or explicit partition 0 must exist" (p. 3-20).
                //  - RB, RM, RMA: "Partition 0, whether implicit or explicit, must exist or the
                //    read command sent as the first byte of the data stream is rejected" (p. 3-18
                //    note 1). A read command in a retry state is a retry instead (p. 3-17, 3-22),
                //    handled above.
                bool eraseWithReset = (command == Command3270.EraseWrite || command == Command3270.EraseWriteAlternate)
                    && record.Length > 1 && (record[1] & Wcc.Reset) != 0;
                if (eraseWithReset)
                {
                    ResetToImplicit(_sessionRows, _sessionColumns);
                }
                else
                {
                    Partition3270? zero = _partitions[0];
                    if (zero == null)
                    {
                        Error("Command X'" + Hex(command) + "' sent as the first byte goes to explicit partition 0, and there is none; it is rejected (GA23-0059-4 Table 3-3 p. 3-4, p. 3-18, 3-20).");
                        return false;
                    }

                    _p = zero;
                }
            }

            switch (command)
            {
                case Command3270.Write:
                    Write(record.Slice(1), erase: false, alternate: false);
                    AcknowledgeAfterWrite(record.Length > 1);
                    break;

                case Command3270.EraseWrite:
                    Write(record.Slice(1), erase: true, alternate: false);
                    AcknowledgeAfterWrite(record.Length > 1);
                    break;

                case Command3270.EraseWriteAlternate:
                    Write(record.Slice(1), erase: true, alternate: true);
                    AcknowledgeAfterWrite(record.Length > 1);
                    break;

                case Command3270.EraseAllUnprotected:
                    // EAU acknowledges a query (p. 10-5) and an enter or read on partition 0
                    // (p. 3-23), and it restores the keyboard anyway.
                    EraseAllUnprotected();
                    break;

                case Command3270.ReadBuffer:
                    SendReadBuffer(_lastAid);
                    break;

                case Command3270.ReadModified:
                    // In a retry state "the read modified command performs a retry of the inbound
                    // operation" (p. 3-14, 3-17); only the Read Modified command does, not Read
                    // Modified All, Read Buffer or a Read Partition (p. 3-14). The kept record is
                    // the inbound operation's own data; with none kept (Attn has sent since), the
                    // data is read again from INPID as INOP Read Modified defines it (p. 3-22 3b).
                    // A trigger field's retry is that field again (p. 4-23: "The RM command
                    // transmits the trigger field").
                    if (retryRead && _retryLength > 0)
                    {
                        InboundRecord?.Invoke(_retryRecord.AsSpan(0, _retryLength));
                        break;
                    }

                    SendReadModified(_lastAid, all: false);
                    if (retryRead)
                    {
                        KeepLastInboundForRetry();
                    }

                    break;

                case Command3270.ReadModifiedAll:
                    SendReadModified(_lastAid, all: true);
                    break;

                case Command3270.WriteStructuredField:
                    // A WSF acknowledges a query "regardless of an error being detected in the
                    // accompanying structured fields" (p. 3-23, 10-5), so the lock ends before the
                    // fields run; a Read Partition Query among them locks it again when it replies.
                    if (_deviceLock == KeyboardLock.QueryReplySent)
                    {
                        _deviceLock = KeyboardLock.None;
                    }

                    WriteStructuredField(record.Slice(1));
                    break;

                default:
                    Error("Unknown command X'" + command.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' (GA23-0059-4 p. 3-2).");
                    break;
            }

            if (_triggerReply == TriggerReply.Negative && !_failed)
            {
                // "Any other operation is a negative reply. The negative reply causes the
                // keystroke queue to be emptied (purged) and an input-inhibit condition to be
                // raised" - the invalid-field condition, with the cursor where the host put it
                // (p. 4-24). "These replies apply only if the command is processed successfully"
                // (p. 4-25), so a record with an error leaves the trigger action waiting. The
                // condition is raised after the record, so a keyboard restore in a negative reply
                // (an Erase/Write is negative whatever its WCC) does not take it down again; that
                // order is this display's reading, the manual does not say.
                _triggerPending = false;
                _queueCount = 0;
                _deviceLock = KeyboardLock.InvalidField;
            }

            _triggerReply = TriggerReply.None;

            // The operator's keys work on the active partition (p. 2-5).
            SelectActive();
            return !_failed;
        }

        // After a W, EW or EWA command sent as the first byte.
        private void AcknowledgeAfterWrite(bool hasWcc)
        {
            if (_failed)
            {
                // "If data is present and an error is detected in the data, the transmission is
                // not an acknowledgment" (p. 3-23, notes 1 and 2).
                return;
            }

            // An EW, EWA or Write acknowledges a query "with or without a WCC or data" (p. 3-23,
            // 10-5). Keyboard restore is not needed; with it, Write has already unlocked.
            if (_deviceLock == KeyboardLock.QueryReplySent)
            {
                _deviceLock = KeyboardLock.None;
            }

            // An enter or a read: "If no WCC is defined following an Erase/Write ... any
            // outstanding read or enter operation is not acknowledged" (p. 3-6); the same is read
            // here for a Write with no WCC, which "is a no-op" (p. 3-2).
            if (hasWcc || _inop == InopQuery)
            {
                AcknowledgeInbound();
            }

            if (_triggerReply == TriggerReply.Affirmative)
            {
                // "The queued keystrokes are processed after the operation is completed, that is,
                // after the orders and data have been processed" (p. 4-23).
                TriggerAffirmative();
            }
        }

        // The host has acknowledged the inbound operation (p. 3-23): back to normal-read state,
        // INOP Read Modified (p. 3-19), nothing kept for a retry, and the inbound partition's
        // PWAIT (TWAIT) ended. System Lock stays: only a write with keyboard restore to its
        // partition or the RESET key removes it (p. 2-9, 3-21).
        //
        // Which transmissions acknowledge. p. 3-23 starts from the SNA rule, "any transmission
        // that after execution leaves the display in a Send or Contention state" and not one that
        // makes the display send input, then lists what else acknowledges. TN3270 carries no SNA
        // request header, so there is no Change Direction or End Bracket to read; every host
        // record is taken here as ending the host's turn, the way this display already took a
        // Write without keyboard restore as ending TWAIT and leaving System Lock (the SNA Change
        // Direction case of p. 3-21; x3270 Common\ctlr.c, ctlr_write, does the same). So every
        // outbound record that is not a read command, and does not itself carry a Read Partition
        // for an enter or a read, acknowledges. That is this display's reading for TN3270, not a
        // sentence of the manual.
        private void AcknowledgeInbound()
        {
            if (_readState == ReadState.Normal)
            {
                return;
            }

            Partition3270? inbound = PartitionFor(_inboundPid);
            if (inbound != null)
            {
                inbound.Pwait = false;
            }

            _readState = ReadState.Normal;
            _inop = Command3270.ReadModified;
            _retryLength = 0;
        }

        // Every input inhibit condition and the read state reset: an SSCP-LU message or NVT text
        // ("It is unlocked when in contention or send-not-xmit", GA23-0061-2 p. 5-36), and the
        // last partition destroyed ("Resets any Input Inhibit conditions", GA23-0059-4 p. 5-14).
        private void ClearAllInputInhibit()
        {
            _deviceLock = KeyboardLock.None;
            _implicitPartition.Pwait = false;
            _implicitPartition.SysLock = false;
            for (int i = 0; i < _partitions.Length; i++)
            {
                Partition3270? p = _partitions[i];
                if (p != null)
                {
                    p.Pwait = false;
                    p.SysLock = false;
                }
            }

            _readState = ReadState.Normal;
            _inop = Command3270.ReadModified;
            _retryLength = 0;
            _triggerPending = false;
            _queueCount = 0;
        }

        /// <summary>
        /// The operator types a character at the cursor (GA27-2890-4 p. 3-25; GA23-0061-2 p. C-3).
        /// </summary>
        /// <param name="c">
        /// The typed character.
        /// </param>
        /// <returns>
        /// False when the key was refused: keyboard locked, character not in the code page, the
        /// cursor in a protected field or on a field attribute, or no room in insert mode (the last
        /// two lock the keyboard).
        /// </returns>
        public bool Type(char c)
        {
            if (Lock != KeyboardLock.None)
            {
                // A real 3278 has no type-ahead; keys pressed while locked are refused
                // (KEYBOARD.md section 5; decided 22 November 2026).
                return false;
            }

            if (!_codePage.TryFromUnicode(c, out byte ebcdic))
            {
                return false;
            }

            if (_triggerPending)
            {
                // While a trigger field is validated "All keystrokes are accepted and queued ...
                // The cursor is not moved, however, and keystrokes are not displayed, until an
                // affirmative reply is received" (p. 4-23).
                return Enqueue(KeystrokeKind.Type, c, 0, 0);
            }

            return TypeByte(ebcdic, tabAfter: false);
        }

        /// <summary>
        /// The DUP key: stores the DUP code X'1C', sets the MDT and tabs to the next input field
        /// (GA23-0061-2 p. C-6, C-7).
        /// </summary>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard).
        /// </returns>
        public bool Dup()
        {
            if (Lock == KeyboardLock.None && _triggerPending)
            {
                return Enqueue(KeystrokeKind.Dup, '\0', 0, 0);
            }

            return Lock == KeyboardLock.None && TypeByte(FormatControl.Dup, tabAfter: true);
        }

        /// <summary>
        /// The FIELD MARK key: stores the FM code X'1E' and sets the MDT; the cursor moves on as
        /// after any character (GA23-0061-2 p. C-7).
        /// </summary>
        /// <returns>
        /// False when refused, as for <see cref="Dup"/>.
        /// </returns>
        public bool FieldMark()
        {
            if (Lock == KeyboardLock.None && _triggerPending)
            {
                return Enqueue(KeystrokeKind.FieldMark, '\0', 0, 0);
            }

            return Lock == KeyboardLock.None && TypeByte(FormatControl.FieldMark, tabAfter: false);
        }

        /// <summary>
        /// The Insert key: switches insert mode on, or off again. Refused while the keyboard is
        /// locked.
        /// </summary>
        public void ToggleInsert()
        {
            if (Lock == KeyboardLock.None)
            {
                if (_triggerPending)
                {
                    Enqueue(KeystrokeKind.ToggleInsert, '\0', 0, 0);
                    return;
                }

                InsertMode = !InsertMode;
            }
        }

        /// <summary>
        /// The Delete key: removes the character at the cursor; the rest of the field on the same
        /// row moves one place left and a null fills the end. The cursor stays (GA23-0061-2 p. C-6).
        /// </summary>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard).
        /// </returns>
        public bool Delete()
        {
            if (Lock == KeyboardLock.None && _triggerPending)
            {
                return Enqueue(KeystrokeKind.Delete, '\0', 0, 0);
            }

            if (Lock != KeyboardLock.None || !CheckInputPosition(out int fa))
            {
                return false;
            }

            // The end is the last position before the next field attribute, never past the row.
            int rowEnd = ((CursorAddress / Columns) * Columns) + Columns - 1;
            int end = CursorAddress;
            while (end < rowEnd && !_p.IsFieldAttributeAt[end + 1])
            {
                end++;
            }

            for (int a = CursorAddress; a < end; a++)
            {
                MoveCell(a + 1, a);
            }

            StoreCharacter(end, FormatControl.Null, graphicEscape: false, useSa: false);
            SetMdt(fa);
            Prime(fa);
            return true;
        }

        /// <summary>
        /// Erase left, the PC Backspace of wc3270: the cursor moves one place left and the
        /// character there is deleted as <see cref="Delete"/> deletes it, so the rest of the field
        /// moves one place left. At the first position of a field nothing happens.
        /// </summary>
        /// <remarks>
        /// IBM's 3278 had no such key: its Backspace only moved the cursor (GA27-2890-4 p. 3-31).
        /// This follows x3270's Erase action, step for step (Common\kybd.c, do_erase, read 23
        /// November 2026): refused in a protected field or on a field attribute; nothing when the
        /// position to the left is the field's attribute; otherwise left one, then delete. wc3270
        /// gives the PC Backspace key this action in its built-in key map (Common\fb-c3270, the
        /// _WIN32 part: "Key BACK: Erase()"). The DBCS steps of do_erase are left out: this
        /// display has no DBCS.
        /// </remarks>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard). True otherwise, also when nothing was erased.
        /// </returns>
        public bool Erase()
        {
            if (Lock == KeyboardLock.None && _triggerPending)
            {
                return Enqueue(KeystrokeKind.Erase, '\0', 0, 0);
            }

            if (Lock != KeyboardLock.None || !CheckInputPosition(out int fa))
            {
                return false;
            }

            int left = CursorAddress == 0 ? Size - 1 : CursorAddress - 1;
            if (fa >= 0 && left == fa)
            {
                return true;
            }

            MoveCursor(left);
            return Delete();
        }

        /// <summary>
        /// The ERASE EOF key: nulls from the cursor to the end of the field, or on an unformatted
        /// screen to the end of the screen. The cursor stays and the MDT is set (GA23-0061-2 p. C-4).
        /// </summary>
        /// <returns>
        /// False when refused: keyboard locked, or the cursor in a protected field or on a field
        /// attribute (which locks the keyboard).
        /// </returns>
        public bool EraseEndOfField()
        {
            if (Lock == KeyboardLock.None && _triggerPending)
            {
                return Enqueue(KeystrokeKind.EraseEndOfField, '\0', 0, 0);
            }

            if (Lock != KeyboardLock.None || !CheckInputPosition(out int fa))
            {
                return false;
            }

            if (fa < 0)
            {
                // Unformatted: to the last position on the bottom line (GA27-2890-4 p. 3-35).
                for (int a = CursorAddress; a < Size; a++)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                }

                return true;
            }

            // It may wrap from the end of the screen to the end of the field.
            int p = CursorAddress;
            while (!_p.IsFieldAttributeAt[p])
            {
                StoreCharacter(p, FormatControl.Null, graphicEscape: false, useSa: false);
                p = Next(p);
            }

            SetMdt(fa);
            Prime(fa);
            return true;
        }

        /// <summary>
        /// The ERASE INPUT key: nulls in every unprotected field, their MDTs reset, the cursor to
        /// the first input position (GA23-0061-2 p. C-5).
        /// </summary>
        /// <remarks>
        /// With only protected fields nothing is erased and the cursor goes to 0; with no fields at
        /// all the whole screen is erased and the cursor goes to 0.
        /// </remarks>
        /// <returns>
        /// False when refused because the keyboard is locked.
        /// </returns>
        public bool EraseInput()
        {
            if (Lock != KeyboardLock.None)
            {
                return false;
            }

            if (_triggerPending)
            {
                return Enqueue(KeystrokeKind.EraseInput, '\0', 0, 0);
            }

            // An erase-input keystroke unprimes a mandatory-fill field (p. 4-21) and a trigger
            // field (p. 4-23). The cursor then goes to the first input field without a trigger
            // action: the field it leaves is no longer primed.
            _p.PrimedField = -1;
            _p.PrimedTrigger = -1;
            int size = Size;
            int first = GetFieldAttributeFor(size - 1);
            if (first < 0)
            {
                for (int a = 0; a < size; a++)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                }

                CursorAddress = 0;
                return true;
            }

            bool isProtected = FieldAttribute.IsProtected(_p.Buffer[first]);
            bool any = false;
            for (int a = 0; a < size; a++)
            {
                if (_p.IsFieldAttributeAt[a])
                {
                    isProtected = FieldAttribute.IsProtected(_p.Buffer[a]);
                    if (!isProtected)
                    {
                        any = true;
                        _p.Buffer[a] = (byte)(_p.Buffer[a] & ~FieldAttribute.Mdt);
                    }
                }
                else if (!isProtected)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                }
            }

            CursorAddress = any ? NextInputPosition(size - 1) : 0;
            return true;
        }

        /// <summary>
        /// Back Tab: the first position of the current input field, or of the previous one when the
        /// cursor is already there, on an attribute or in a protected field; no input field: 0
        /// (GA23-0061-2 p. C-4).
        /// </summary>
        public void BackTab()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            if (_triggerPending)
            {
                Enqueue(KeystrokeKind.BackTab, ' ', 0, 0);
                return;
            }

            int size = Size;
            int fa = GetFieldAttributeFor(CursorAddress);
            if (fa >= 0 && !_p.IsFieldAttributeAt[CursorAddress] && !FieldAttribute.IsProtected(_p.Buffer[fa]) && Next(fa) != CursorAddress)
            {
                OperatorMove(Next(fa));
                return;
            }

            // Search backwards from the position before the current field start.
            int a = CursorAddress;
            for (int n = 0; n < size; n++)
            {
                a = a == 0 ? size - 1 : a - 1;
                if (_p.IsFieldAttributeAt[a] && !FieldAttribute.IsProtected(_p.Buffer[a]) && Next(a) != CursorAddress && !_p.IsFieldAttributeAt[Next(a)])
                {
                    OperatorMove(Next(a));
                    return;
                }
            }

            OperatorMove(0);
        }

        /// <summary>
        /// New Line: the first input position on the next line or after; no input field: 0; no
        /// fields at all: the first position of the next line (GA23-0061-2 p. C-4).
        /// </summary>
        public void NewLine()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            if (_triggerPending)
            {
                Enqueue(KeystrokeKind.NewLine, ' ', 0, 0);
                return;
            }

            int size = Size;
            int lineStart = ((CursorAddress / Columns) + 1) * Columns;
            if (lineStart >= size)
            {
                lineStart = 0;
            }

            if (!IsFormatted)
            {
                OperatorMove(lineStart);
                return;
            }

            int a = lineStart;
            for (int n = 0; n < size; n++)
            {
                int fa = GetFieldAttributeFor(a);
                if (!_p.IsFieldAttributeAt[a] && fa >= 0 && !FieldAttribute.IsProtected(_p.Buffer[fa]))
                {
                    OperatorMove(a);
                    return;
                }

                a = Next(a);
            }

            OperatorMove(0);
        }

        /// <summary>
        /// The arrow keys and Backspace: one position, wrapping to the row above or below at the
        /// edges and from top to bottom in the same column (GA23-0061-2 p. C-3). The MDT is not
        /// touched.
        /// </summary>
        /// <param name="columns">
        /// Positions to the right, negative to the left (ALT gives two).
        /// </param>
        /// <param name="rows">
        /// Rows down, negative up.
        /// </param>
        public void MoveCursorBy(int columns, int rows)
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            if (_triggerPending)
            {
                Enqueue(KeystrokeKind.MoveBy, ' ', columns, rows);
                return;
            }

            // "The cursor movement keys (Up, Down, Left, Right, Backspace) wrap at the boundary of
            // the viewport" (p. 7-16, 7-17), so the arithmetic is on the window, which is the
            // whole presentation space when the partition does not scroll.
            int top = _p.WindowRow * Columns;
            int windowRows = _p.ViewportRows;
            int windowSize = windowRows * Columns;
            int a = CursorAddress - top + columns;
            a = ((a % windowSize) + windowSize) % windowSize;
            int row = (a / Columns) + rows;
            row = ((row % windowRows) + windowRows) % windowRows;
            OperatorMove(top + (row * Columns) + (a % Columns));
        }

        /// <summary>
        /// The scroll-up key: the window moves down the presentation space by the partition's
        /// RS rows, so the data moves up the viewport (GA23-0059-4 p. 7-16, 7-17).
        /// </summary>
        /// <remarks>
        /// <para>
        /// "If the current cursor position is in the top row of the window, the cursor is moved
        /// down the presentation space so that it remains in the top row of the window"; at the
        /// bottom of the presentation space "scrolling up has no effect. Note that there is no
        /// input-inhibit condition and no indicator" (p. 7-17). A scroll that drags the cursor out
        /// of a primed trigger field is a trigger action (p. 4-25 note 1).
        /// </para>
        /// <para>
        /// Choices of this display, not settled by the manual: a scroll that would pass the end
        /// moves only to the end; RS X'FFFF' ("the device determines the amount", p. 5-12) moves
        /// one viewport height; RS 0 moves nothing; and the scroll keys are refused while the
        /// keyboard is locked, as the cursor keys are.
        /// </para>
        /// </remarks>
        /// <returns>
        /// True when the window moved.
        /// </returns>
        public bool ScrollUp() => Scroll(1);

        /// <summary>
        /// The scroll-down key: the window moves up the presentation space by RS rows, so the
        /// data moves down the viewport (GA23-0059-4 p. 7-17). See <see cref="ScrollUp"/>.
        /// </summary>
        /// <returns>
        /// True when the window moved.
        /// </returns>
        public bool ScrollDown() => Scroll(-1);

        /// <summary>
        /// The jump-partition key: the next partition becomes the active one and the cursor shows
        /// at its current cursor position (GA23-0059-4 p. 2-5).
        /// </summary>
        /// <remarks>
        /// <para>
        /// p. 2-5: "If the partition ID has the value N, the next active partition is that partition
        /// with the smallest PID greater than N. If no such partition exists, the next active
        /// partition is that partition with the lowest PID." The partition left keeps its PWAIT and
        /// System Lock and shows them again when it is active again (p. 2-9). "When the TWAIT
        /// condition exists, the Jump key does nothing; it is a No-op" (p. 3-20): TWAIT is the
        /// condition of implicit partition state and of a single partition, and the TWAIT after a
        /// query reply.
        /// </para>
        /// <para>
        /// A jump while a trigger field is validated is not queued and starts no trigger action;
        /// the partition left keeps its cursor in the trigger field (p. 4-25 notes 3 and 7). The
        /// display's other conditions (operator errors and the like) do not stop the jump: the
        /// manual does not say they do, and Activate Partition leaves them shown (p. 5-8 step 5b).
        /// That is this display's reading.
        /// </para>
        /// </remarks>
        /// <returns>
        /// True when another partition became active.
        /// </returns>
        public bool JumpPartition()
        {
            if (!_explicitState || CountPartitions() < 2 || _deviceLock == KeyboardLock.QueryReplySent)
            {
                return false;
            }

            _activePid = NextPartitionAfter(_activePid);
            SelectActive();
            return true;
        }

        private bool Scroll(int direction)
        {
            if (Lock != KeyboardLock.None)
            {
                return false;
            }

            if (_triggerPending)
            {
                return Enqueue(KeystrokeKind.Scroll, ' ', direction, 0);
            }

            Partition3270 p = _p;
            int lastWindowRow = p.Rows - p.ViewportRows;
            int step = p.ScrollRows == 0xFFFF ? p.ViewportRows : p.ScrollRows;
            int windowRow = Math.Clamp(p.WindowRow + (direction * step), 0, lastWindowRow);
            if (windowRow == p.WindowRow)
            {
                return false;
            }

            // The cursor is dragged along at the edge of the window (p. 7-16).
            int row = p.Cursor / p.Columns;
            int draggedRow = Math.Clamp(row, windowRow, windowRow + p.ViewportRows - 1);
            int target = (draggedRow * p.Columns) + (p.Cursor % p.Columns);
            if (target != p.Cursor)
            {
                int fa = GetFieldAttributeFor(target);
                if (p.PrimedField >= 0 && fa != p.PrimedField && !ValidateFill())
                {
                    // A scroll that would drag the cursor out of a primed mandatory-fill field
                    // that fails validation does not happen: the cursor must stay in the field
                    // (p. 4-21). This display's reading.
                    return false;
                }

                if (p.PrimedTrigger >= 0 && fa != p.PrimedTrigger)
                {
                    return TriggerAction(target, windowRow, forMandatoryEntry: false);
                }
            }

            p.WindowRow = windowRow;
            p.Cursor = target;
            return true;
        }

        /// <summary>
        /// Tab: first character of the next input field; none, or an unformatted screen: address 0
        /// (GA27-2890-4 p. 3-28).
        /// </summary>
        public void Tab()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            if (_triggerPending)
            {
                Enqueue(KeystrokeKind.Tab, ' ', 0, 0);
                return;
            }

            OperatorMove(NextInputPosition(CursorAddress));
        }

        /// <summary>
        /// Home: the first input position on the screen; none: address 0 (GA27-2890-4 p. 3-30).
        /// </summary>
        public void Home()
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            if (_triggerPending)
            {
                Enqueue(KeystrokeKind.Home, ' ', 0, 0);
                return;
            }

            int last = Size - 1;
            OperatorMove(NextInputPosition(last));
        }

        /// <summary>
        /// End (Field End): the cursor goes just after the last character in its field that is not
        /// a blank or a null, or to the field's first position when it holds none.
        /// </summary>
        /// <remarks>
        /// The 3278 keyboard has no such key; the 3270 PC and x3270 do. Taken from x3270
        /// (Common\kybd.c, FieldEnd_action): nothing happens on an unformatted screen, on a field
        /// attribute or in a protected field, and when the last character is in the field's last
        /// position the cursor stays on it rather than landing on the next attribute.
        /// </remarks>
        public void FieldEnd()
        {
            if (Lock != KeyboardLock.None || !IsFormatted)
            {
                return;
            }

            if (_triggerPending)
            {
                Enqueue(KeystrokeKind.FieldEnd, ' ', 0, 0);
                return;
            }

            int fa = GetFieldAttributeFor(CursorAddress);
            if (fa == CursorAddress || FieldAttribute.IsProtected(_p.Buffer[fa]))
            {
                return;
            }

            int lastNonBlank = -1;
            for (int a = Next(fa); !_p.IsFieldAttributeAt[a]; a = Next(a))
            {
                byte c = _p.Buffer[a];
                if (c != FormatControl.Null && c != EbcdicSpace)
                {
                    lastNonBlank = a;
                }
            }

            int target = lastNonBlank < 0 ? Next(fa) : Next(lastNonBlank);
            if (lastNonBlank >= 0 && _p.IsFieldAttributeAt[target])
            {
                target = lastNonBlank;
            }

            OperatorMove(target);
        }

        /// <summary>
        /// Cursor Select: selects the selector-pen detectable field the cursor is in, by its
        /// designator character, the first character of the field.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4 p. 4-22 to 4-24 and x3270 (Common\kybd.c, lightpen_select):
        ///  - "?" becomes ">" and the field's MDT is set (selected).
        ///  - ">" becomes "?" and the MDT is cleared (deselected).
        ///  - a space or a null: MDT set, then a selector-pen attention (AID X'7E'), which sends the
        ///    modified fields' addresses without their data (p. 3-12).
        ///  - an ampersand: MDT set, then an Enter.
        /// A field that is not detectable (display bits other than 01 or 10), or any other
        /// designator, locks the keyboard with Go Elsewhere, as GA27-2890-4 p. 2-22 says for "a
        /// cursor select operation when the cursor is not in a valid cursor select field"; x3270
        /// only rings the bell there. RetroIO follows IBM, as it does for typing on a protected
        /// field.
        /// </remarks>
        /// <returns>
        /// True when the field was selected, deselected or sent; false when refused.
        /// </returns>
        public bool CursorSelect()
        {
            if (Lock != KeyboardLock.None)
            {
                return false;
            }

            if (_triggerPending)
            {
                // Queued whole, as the other keystrokes are (p. 4-23); what it does is decided
                // when it is played back. This display's reading: the manual does not name the
                // selector pen among the keys that are not queued.
                return Enqueue(KeystrokeKind.CursorSelect, ' ', 0, 0);
            }

            int fa = GetFieldAttributeFor(CursorAddress);
            byte display = fa >= 0 ? (byte)(_p.Buffer[fa] & FieldAttribute.DisplayMask) : (byte)0;
            if (fa < 0 || (display != 0x04 && display != 0x08))
            {
                _deviceLock = KeyboardLock.OperatorError;
                return false;
            }

            int designator = Next(fa);
            switch (_p.Buffer[designator])
            {
                case EbcdicQuestion:
                    _p.Buffer[designator] = EbcdicGreater;
                    _p.Buffer[fa] = (byte)(_p.Buffer[fa] | FieldAttribute.Mdt);
                    return true;
                case EbcdicGreater:
                    _p.Buffer[designator] = EbcdicQuestion;
                    _p.Buffer[fa] = (byte)(_p.Buffer[fa] & ~FieldAttribute.Mdt);
                    return true;
                case EbcdicSpace:
                case FormatControl.Null:
                    _p.Buffer[fa] = (byte)(_p.Buffer[fa] | FieldAttribute.Mdt);
                    return Attention(Aid.SelectorPen);
                case EbcdicAmpersand:
                    _p.Buffer[fa] = (byte)(_p.Buffer[fa] | FieldAttribute.Mdt);
                    return Attention(Aid.Enter);
                default:
                    _deviceLock = KeyboardLock.OperatorError;
                    return false;
            }
        }

        /// <summary>
        /// Sys Req (Test Req on the 3278): sends a test request read and waits for the host.
        /// </summary>
        /// <remarks>
        /// GA23-0059-4 p. 9-3: the heading is SOH % / STX (X'01' X'6C' X'61' X'02') instead of the
        /// AID and cursor address; the rest is a Read Modified's data (every modified field, nulls
        /// left out; all data on an unformatted screen). The manual's non-SNA layout ends with ETX;
        /// x3270 (Common\ctlr.c, ctlr_read_modified, case AID_SYSREQ) sends no ETX over TN3270,
        /// and neither does RetroIO. Under TN3270E with the SYSREQ function Sys Req is not this
        /// read but a telnet Abort Output that the connection sends (RFC 2355 section 10.5.2);
        /// the SSCP's answer then comes through <see cref="ProcessSscpLuData"/>.
        /// While a trigger field is validated, "system request purges the queue" of keystrokes
        /// (GA23-0059-4 p. 4-23); the read itself is then an enter action like any other, refused
        /// under Enter Inhibit (p. 3-21 step 1).
        /// </remarks>
        /// <returns>
        /// True when sent; false when the keyboard is locked.
        /// </returns>
        public bool SysReq()
        {
            _queueCount = 0;
            return Attention(Aid.SysReq);
        }

        /// <summary>
        /// The data-stream half of Attn in plain TN3270: the PA1 short read, sent whatever the
        /// keyboard lock is, and the lock left as it was.
        /// </summary>
        /// <remarks>
        /// Taken from x3270 (Common\kybd.c, Attn_action, the IN_3270 case, commented "This is
        /// what PCOMM does in plain TN3270 mode: PA1, then TELNET BREAK"): ctlr_read_modified with
        /// AID_PA1, then net_break. The telnet break is the connection's job, not the display's;
        /// the caller sends it after this. Attn works while the keyboard is locked, because
        /// interrupting a host that does not answer is what it is for. No IBM manual held here
        /// describes Attn on a TN3270 connection, so x3270 is the only source.
        /// </remarks>
        public void Attn() => SendReadModified(Aid.PA1, all: false);

        /// <summary>
        /// Refuses a key whose function is not available now: the keyboard locks with Minus
        /// Function ("X -f") until Reset. Nothing is sent.
        /// </summary>
        /// <remarks>
        /// See <see cref="KeyboardLock.MinusFunction"/> for where this comes from.
        /// </remarks>
        public void RefuseFunction() => _deviceLock = KeyboardLock.MinusFunction;

        /// <summary>
        /// Pastes text the way x3270's normal paste does: typed character by character, with a
        /// line break pressing New Line (the first input field on a following row).
        /// </summary>
        /// <remarks>
        /// From x3270 (Common\kybd.c, emulate_uinput with pasting set, overlay and margined paste
        /// off, which are its defaults):
        ///  - a line feed presses New Line, unless the text just wrapped onto a new row by itself.
        ///  - a carriage return does nothing, so CR LF counts once.
        ///  - tab presses Tab, backspace moves left, form feed types a space.
        ///  - everything else is typed, with the auto-skip Type already does.
        /// The paste stops when the keyboard locks (a character on a protected field is an
        /// operator error, as when typing it) and when the cursor wraps past the end of the screen
        /// back above where it started. The rest of the text is dropped, as x3270 drops it.
        /// </remarks>
        /// <param name="text">
        /// The pasted text.
        /// </param>
        public void Paste(ReadOnlySpan<char> text)
        {
            int columns = Columns;
            int start = CursorAddress;
            int lastAddress = start;
            int lastRow = start / columns;
            bool justWrapped = false;
            for (int i = 0; i < text.Length; i++)
            {
                if (Lock != KeyboardLock.None || CursorAddress < start)
                {
                    return;
                }

                if (lastAddress != CursorAddress)
                {
                    lastAddress = CursorAddress;
                    int row = CursorAddress / columns;
                    justWrapped = row != lastRow;
                    lastRow = row;
                }

                char c = text[i];
                switch (c)
                {
                    case '\n':
                        if (!justWrapped)
                        {
                            NewLine();
                        }

                        lastRow = CursorAddress / columns;
                        justWrapped = false;
                        break;
                    case '\r':
                        break;
                    case '\t':
                        Tab();
                        break;
                    case '\b':
                        MoveCursorBy(-1, 0);
                        break;
                    case '\f':
                        Type(' ');
                        break;
                    default:
                        Type(c);
                        break;
                }
            }
        }

        /// <summary>
        /// Moves the cursor to an address (mouse click, arrow keys). Refused while locked.
        /// </summary>
        /// <param name="address">
        /// The buffer address.
        /// </param>
        public void MoveCursor(int address)
        {
            if (Lock != KeyboardLock.None)
            {
                return;
            }

            if (address < 0 || address >= Size)
            {
                throw new ArgumentOutOfRangeException(nameof(address));
            }

            if (_triggerPending)
            {
                Enqueue(KeystrokeKind.MoveTo, ' ', address, 0);
                return;
            }

            OperatorMove(address);
        }

        /// <summary>
        /// Reset key: clears an operator error or a System Lock and ends insert mode. It does not
        /// clear waiting for the host (GA23-0059-4 p. 3-20; GA27-2890-4 p. 2-21 and 3-40;
        /// GA23-0061-2 p. C-6).
        /// </summary>
        public void Reset()
        {
            // The operator-clearable conditions of the display. InvalidField and
            // KeystrokeQueueFull are cleared here by this display's own choice (p. 4-24 and 4-25
            // do not say what ends them). Reset is not queued while a trigger field is validated:
            // the manual names it among the keys that act at once (this display's reading of
            // p. 4-23, which queues "all keystrokes" but lists the input-inhibit keys apart).
            KeyboardLock device = _deviceLock;
            if (device == KeyboardLock.OperatorError || device == KeyboardLock.TooMuchData || device == KeyboardLock.MandatoryFill
                || device == KeyboardLock.MandatoryEntry || device == KeyboardLock.MinusFunction
                || device == KeyboardLock.InvalidField || device == KeyboardLock.KeystrokeQueueFull)
            {
                _deviceLock = KeyboardLock.None;
            }

            // "The RESET key removes System Lock when the partition in which it exists is the
            // active partition" (p. 2-9), but not while PWAIT hides it: Reset does not end a
            // wait (p. 3-20).
            if (_deviceLock == KeyboardLock.None && !_p.Pwait)
            {
                _p.SysLock = false;
            }

            if (Lock == KeyboardLock.None)
            {
                InsertMode = false;
            }
        }

        /// <summary>
        /// An attention key: Enter, PF1-PF24, PA1-PA3 or Clear. Sends the reply to the host and
        /// locks the keyboard until the host restores it (GA23-0059-4 p. 3-8, 3-12, 3-13).
        /// </summary>
        /// <param name="aid">
        /// The AID byte, from <see cref="Aid"/>.
        /// </param>
        /// <returns>
        /// False when refused because the keyboard is locked.
        /// </returns>
        public bool Attention(byte aid)
        {
            if (Lock != KeyboardLock.None)
            {
                return false;
            }

            if (SscpLuMode)
            {
                return SscpAttention(aid);
            }

            if (NvtMode)
            {
                return NvtAttention(aid);
            }

            if (_triggerPending && aid != Aid.SysReq)
            {
                // An AID key while a trigger field is validated is a keystroke like the others:
                // queued, and run after the affirmative reply, which has acknowledged the trigger
                // action by then (p. 4-23). Sys Req is not queued; it purges the queue.
                return Enqueue(KeystrokeKind.Attention, ' ', aid, 0);
            }

            if (_readState != ReadState.Normal)
            {
                // "If the Enter Inhibit condition exists, the enter action is rejected" (p. 3-21
                // step 1): an inbound operation of some partition is not yet acknowledged. Which
                // indicator a real display then shows is not stated; this display shows none and
                // only refuses the key.
                return false;
            }

            if (aid == Aid.Clear)
            {
                // Clear: every field attribute becomes a null - no fields left (p. 4-13), the buffer
                // and all extended attributes are cleared, the screen goes back to the default size
                // and the cursor to 0 (GA23-0061-2 p. C-8). Then the short read: the AID alone.
                // In explicit partitioned state Clear also destroys every partition and goes back
                // to implicit partition 0 (GA23-0059-4 p. 3-16: implicit partition 0 occurs
                // "After the CLEAR key is pressed"; Figure 2-4, p. 2-8), so the short read comes
                // from partition 0 in plain 3270 format.
                ResetToImplicit(_sessionRows, _sessionColumns);

                // And the inbound reply mode goes back to field mode: GA23-0059-4 Table C-1
                // (p. C-1), row "CLEAR Key LULU", Inbound Reply Mode = R. x3270 does not do this
                // (it resets the mode only on connect and on an Erase/Write with WCC reset), but the
                // manual is the authority. A TN3270 host session is the LU-LU case; the "Unowned"
                // row says implementation defined.
                // ResetToImplicit has done it.
            }

            if (aid != Aid.Clear && !Aid.IsShortRead(aid))
            {
                // An enter action with the cursor in a primed mandatory-fill field validates it
                // first (p. 4-22 note 4); then no data goes while an unprotected mandatory-entry
                // field has its MDT off, and the cursor goes there (p. 4-22).
                if (!ValidateFill())
                {
                    return false;
                }

                int empty = FirstEmptyMandatoryEntryField();
                if (empty >= 0)
                {
                    CursorAddress = Next(empty);
                    _deviceLock = KeyboardLock.MandatoryEntry;
                    return false;
                }
            }

            // Every key that talks to the host ends insert mode (GA23-0061-2 p. C-6).
            InsertMode = false;
            _lastAid = aid;

            // "INPID is set to the PID of the active partition" (GA23-0059-4 p. 3-21 step 6);
            // the data then comes from that partition, in an Inbound 3270DS when its PID is not 0
            // (p. 6-7, case 3).
            // Steps 5 to 11: Enter Inhibit (the retry-enter state), INOP Read Modified, PWAIT and
            // System Lock on INPID, then the data, kept for a retry by Read Modified (p. 3-17).
            _inboundPid = _activePid;
            _inop = Command3270.ReadModified;
            _p.Pwait = true;
            _p.SysLock = true;
            _readState = ReadState.RetryEnter;
            SendReadModified(aid, all: false);
            KeepLastInboundForRetry();
            return true;
        }

        // The trigger action (p. 4-23): the operator tried to move the cursor out of the primed
        // trigger field, to target (and, for a scroll key, the window to windowRow; -1 keeps it).
        // The cursor stays; the field goes to the host; keystrokes are queued until the reply.
        private bool TriggerAction(int target, int windowRow, bool forMandatoryEntry)
        {
            if (_readState != ReadState.Normal)
            {
                // A trigger action is an enter action (p. 3-21: steps 8 and 9 name it), so Enter
                // Inhibit rejects it like any other. This display's reading: the key is refused
                // and the cursor stays in the field.
                return false;
            }

            Partition3270 p = _p;
            int fa = p.PrimedTrigger;

            // "The trigger action unprimes the field" (p. 4-23).
            p.PrimedTrigger = -1;
            InsertMode = false;

            // Steps 5 to 7, not 8 and 9: no PWAIT and no System Lock for a trigger action
            // (p. 3-21).
            _inboundPid = _activePid;
            _inop = Command3270.ReadModified;
            _readState = ReadState.RetryEnter;
            _triggerPending = true;
            _triggerPid = _activePid;
            _triggerTarget = target;
            _triggerWindowRow = windowRow;
            _triggerForMandatoryEntry = forMandatoryEntry;
            _queueCount = 0;

            // The record: AID X'7F', the cursor address, then the field - "the trigger field's
            // address and data" (p. 4-23), sent as a Read Modified sends one field: SBA to the
            // position after the attribute, then the data with nulls left out (p. 3-10). The
            // MDT is not looked at and not changed (p. 4-26 note 9). The SBA form is this
            // display's reading of "field address".
            _inbound.Clear();
            Array.Clear(_inboundSa);
            BeginInbound(Aid.Trigger);
            AppendAddress(p.Cursor);
            AppendByte(Order3270.SetBufferAddress);
            AppendAddress(Next(fa));
            for (int a = Next(fa); !p.IsFieldAttributeAt[a] && a != fa; a = Next(a))
            {
                byte c = p.Buffer[a];
                if (c != FormatControl.Null)
                {
                    AppendByte(c);
                }
            }

            Send();
            KeepLastInboundForRetry();
            return true;
        }

        // What a host record is to the pending trigger action (p. 4-24). Decided from the
        // record's first bytes before it is applied.
        private TriggerReply ClassifyTriggerReply(ReadOnlySpan<byte> record)
        {
            byte command = record[0];
            switch (command)
            {
                case Command3270.ReadBuffer:
                case Command3270.ReadModified:
                case Command3270.ReadModifiedAll:
                    // "The read commands ... are not replies" (p. 4-24).
                    return TriggerReply.None;

                case Command3270.Write:
                    // "a write command with keyboard restore, directed to INPID": W as the first
                    // byte goes to partition 0 (Table 3-3), so only when INPID is 0.
                    return _triggerPid == 0 && record.Length > 1 && (record[1] & Wcc.KeyboardRestore) != 0
                        ? TriggerReply.Affirmative
                        : TriggerReply.Negative;

                case Command3270.WriteStructuredField:
                    return ClassifyTriggerWsf(record.Slice(1));

                default:
                    // Erase/Write, EWA and EAU are "any other operation" (p. 4-24).
                    return TriggerReply.Negative;
            }
        }

        private TriggerReply ClassifyTriggerWsf(ReadOnlySpan<byte> fields)
        {
            // The first structured field decides: an Outbound 3270DS (X'40') to INPID holding a
            // Write with keyboard restore is affirmative (p. 4-24). A Read Partition is not a
            // reply (p. 4-24) - and is rejected anyway under Enter Inhibit. Anything else is
            // negative.
            if (fields.Length < 3)
            {
                return TriggerReply.Negative;
            }

            byte id = fields[2];
            if (id == StructuredFieldId.ReadPartition)
            {
                return TriggerReply.None;
            }

            if (id == StructuredFieldId.Outbound3270DS && fields.Length >= 6 && fields[3] == _triggerPid
                && fields[4] == Command3270.Write && (fields[5] & Wcc.KeyboardRestore) != 0)
            {
                return TriggerReply.Affirmative;
            }

            return TriggerReply.Negative;
        }

        // The affirmative reply has been applied (p. 4-23, 4-24): the cursor goes where the
        // keystroke that caused the trigger action would have put it, unless the reply moved it
        // with an Insert Cursor order; then the queued keystrokes run.
        private void TriggerAffirmative()
        {
            _triggerPending = false;
            Partition3270? p = PartitionFor(_triggerPid);
            if (p != null)
            {
                Partition3270 saved = _p;
                _p = p;
                if (_triggerForMandatoryEntry)
                {
                    // p. 4-25 note 2: the cursor moved by the mandatory-entry check goes to the
                    // empty mandatory-entry field and the condition is raised after the reply.
                    // This display reads that move as winning over an Insert Cursor order.
                    CursorAddress = _triggerTarget;
                    _deviceLock = KeyboardLock.MandatoryEntry;
                }
                else if (!_insertCursorSeen)
                {
                    if (_triggerWindowRow >= 0)
                    {
                        p.WindowRow = _triggerWindowRow;
                        p.Cursor = _triggerTarget;
                    }
                    else
                    {
                        CursorAddress = _triggerTarget;
                    }
                }

                _p = saved;
            }

            SelectActive();
            ReplayQueue();
        }

        private bool Enqueue(KeystrokeKind kind, char c, int a, int b)
        {
            if (_queueCount >= _queue.Length)
            {
                _deviceLock = KeyboardLock.KeystrokeQueueFull;
                return false;
            }

            _queue[_queueCount++] = new Keystroke(kind, c, a, b);
            return true;
        }

        private void ReplayQueue()
        {
            int count = _queueCount;
            _queueCount = 0;
            Keystroke[] copy = System.Buffers.ArrayPool<Keystroke>.Shared.Rent(Math.Max(count, 1));
            Array.Copy(_queue, copy, count);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    // A keystroke that starts another trigger action queues the rest behind it.
                    if (_triggerPending)
                    {
                        for (int j = i; j < count; j++)
                        {
                            Keystroke k = copy[j];
                            Enqueue(k.Kind, k.Character, k.A, k.B);
                        }

                        return;
                    }

                    RunKeystroke(copy[i]);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<Keystroke>.Shared.Return(copy);
            }
        }

        private void RunKeystroke(Keystroke k)
        {
            switch (k.Kind)
            {
                case KeystrokeKind.Type: Type(k.Character); break;
                case KeystrokeKind.Dup: Dup(); break;
                case KeystrokeKind.FieldMark: FieldMark(); break;
                case KeystrokeKind.ToggleInsert: ToggleInsert(); break;
                case KeystrokeKind.Delete: Delete(); break;
                case KeystrokeKind.Erase: Erase(); break;
                case KeystrokeKind.EraseEndOfField: EraseEndOfField(); break;
                case KeystrokeKind.EraseInput: EraseInput(); break;
                case KeystrokeKind.BackTab: BackTab(); break;
                case KeystrokeKind.NewLine: NewLine(); break;
                case KeystrokeKind.Tab: Tab(); break;
                case KeystrokeKind.Home: Home(); break;
                case KeystrokeKind.FieldEnd: FieldEnd(); break;
                case KeystrokeKind.MoveBy: MoveCursorBy(k.A, k.B); break;
                case KeystrokeKind.MoveTo: MoveCursor(k.A); break;
                case KeystrokeKind.Scroll: Scroll(k.A); break;
                case KeystrokeKind.CursorSelect: CursorSelect(); break;
                case KeystrokeKind.Attention: Attention((byte)k.A); break;
            }
        }

        private enum KeystrokeKind : byte
        {
            Type,
            Dup,
            FieldMark,
            ToggleInsert,
            Delete,
            Erase,
            EraseEndOfField,
            EraseInput,
            BackTab,
            NewLine,
            Tab,
            Home,
            FieldEnd,
            MoveBy,
            MoveTo,
            Scroll,
            CursorSelect,
            Attention,
        }

        private readonly struct Keystroke
        {
            public Keystroke(KeystrokeKind kind, char character, int a, int b)
            {
                Kind = kind;
                Character = character;
                A = a;
                B = b;
            }

            public KeystrokeKind Kind { get; }

            public char Character { get; }

            public int A { get; }

            public int B { get; }
        }

        private enum ReadState : byte
        {
            Normal,
            RetryEnter,
            RetryRead,
        }

        private enum TriggerReply : byte
        {
            None,
            Affirmative,
            Negative,
        }

        /// <summary>
        /// Sends one structured field to the host as its own inbound record: AID X'88', then the
        /// field (GA23-0059-4 p. 3-10).
        /// </summary>
        /// <remarks>
        /// For the replies of <see cref="IndFileTransfer"/>; any other inbound structured field a
        /// caller builds goes the same way.
        /// </remarks>
        /// <param name="field">
        /// The whole structured field, its two length bytes first.
        /// </param>
        public void SendStructuredFieldReply(ReadOnlySpan<byte> field) => SendStructuredFieldReply(field, retry: false);

        /// <summary>
        /// Sends one structured field to the host as its own inbound record, as
        /// <see cref="SendStructuredFieldReply(ReadOnlySpan{byte})"/>, and when asked keeps it so
        /// that a Read Modified command before the host's next other transmission sends the very
        /// same record again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// For the file data <see cref="IndFileTransfer"/> sends on the host's Get. TK5's IND$FILE
        /// answers every full upload block with a Read Modified command (X'F6') and then expects
        /// the block a second time; a terminal that answers with an ordinary Read Modified loses
        /// that block without any error (measured 24 November 2026 on TK5: RetroTerm sent a
        /// 16384-byte block, answered the X'F6' with AID X'88' alone, and the data set held only
        /// the second block; s3270 sent each block again and the file came back whole).
        /// </para>
        /// <para>
        /// This is the retry of GA23-0059-4 p. 3-14 to 3-18: a Read Modified command in a retry
        /// state repeats the last inbound operation, and any outbound transmission other than a
        /// read command acknowledges it (p. 3-23). The manual names only an enter action and a
        /// Read Partition as starting a retry state; IBM's IND$FILE description does not mention
        /// this exchange. x3270 does the same for upload data only (Common/ft_dft.c, the
        /// saved upload buffer sent again on a Read Modified).
        /// </para>
        /// </remarks>
        /// <param name="field">
        /// The whole structured field, its two length bytes first.
        /// </param>
        /// <param name="retry">
        /// True to keep the record for a retry; false sends it without, and ends any retry kept
        /// from before.
        /// </param>
        public void SendStructuredFieldReply(ReadOnlySpan<byte> field, bool retry)
        {
            _inbound.Clear();
            AppendByte(Aid.StructuredField);
            AppendBytes(field);
            Send();
            if (retry)
            {
                // Kept as a read reply is (p. 3-17, 3-18): retry-read state, INOP Read Modified,
                // so a Read Modified command sends it again until the host's next other
                // transmission. Treating this inbound record as a read's reply is this display's
                // reading; the manual starts a retry state only from an enter action or a Read
                // Partition (see above).
                EnterRetryRead(Command3270.ReadModified);
            }
            else if (_readState == ReadState.RetryRead)
            {
                // A new inbound record that is not kept ends the retry of the one before.
                _readState = ReadState.Normal;
                _inop = Command3270.ReadModified;
                _retryLength = 0;
            }
        }

        private void OperatorMove(int address)
        {
            // Moving the cursor out of a primed mandatory-fill field validates it first; on failure
            // the cursor stays in the field (p. 4-21).
            int target = GetFieldAttributeFor(address);
            if (_p.PrimedField >= 0 && target != _p.PrimedField && !ValidateFill())
            {
                return;
            }

            // Fill checking comes first, then the trigger (p. 4-25 note 1): an attempt to move
            // the cursor out of a primed trigger field is the trigger action, and the cursor
            // stays in the field until the host replies (p. 4-23).
            if (_p.PrimedTrigger >= 0 && target != _p.PrimedTrigger)
            {
                TriggerAction(address, -1, forMandatoryEntry: false);
                return;
            }

            CursorAddress = address;
        }

        // Automatic scrolling (p. 7-17): when the cursor is put outside the window - by a field
        // key, a data key, Home, or an Insert Cursor order - the window moves by the fewest rows
        // that bring the cursor in. The window column is always 0.
        private static void ScrollToCursor(Partition3270 p)
        {
            if (!p.Scrolls)
            {
                return;
            }

            int row = p.Cursor / p.Columns;
            if (row < p.WindowRow)
            {
                p.WindowRow = row;
            }
            else if (row >= p.WindowRow + p.ViewportRows)
            {
                p.WindowRow = row - p.ViewportRows + 1;
            }
        }

        private void Prime(int fieldAttribute)
        {
            // An unprotected mandatory-fill field is primed by a data key, Delete or Erase EOF in
            // it (p. 4-21). Protected fields cannot get here.
            if (fieldAttribute >= 0 && (_p.FieldValidationAt[fieldAttribute] & FieldValidation.MandatoryFill) != 0)
            {
                _p.PrimedField = fieldAttribute;
            }

            // A trigger field is primed the same way (p. 4-23).
            if (fieldAttribute >= 0 && (_p.FieldValidationAt[fieldAttribute] & FieldValidation.Trigger) != 0)
            {
                _p.PrimedTrigger = fieldAttribute;
            }
        }

        private bool ValidateFill()
        {
            // A DUP anywhere in the field passes; otherwise any null fails (p. 4-21).
            if (_p.PrimedField < 0)
            {
                return true;
            }

            bool hasNull = false;
            int a = Next(_p.PrimedField);
            int guard = Size;
            while (!_p.IsFieldAttributeAt[a] && guard-- > 0)
            {
                if (_p.Buffer[a] == FormatControl.Dup)
                {
                    _p.PrimedField = -1;
                    return true;
                }

                hasNull |= _p.Buffer[a] == FormatControl.Null;
                a = Next(a);
            }

            if (hasNull)
            {
                _deviceLock = KeyboardLock.MandatoryFill;
                return false;
            }

            _p.PrimedField = -1;
            return true;
        }

        private int FirstEmptyMandatoryEntryField()
        {
            // The first unprotected mandatory-entry field with its MDT off, in buffer order (p. 4-22).
            int size = Size;
            for (int a = 0; a < size; a++)
            {
                if (_p.IsFieldAttributeAt[a]
                    && (_p.FieldValidationAt[a] & FieldValidation.MandatoryEntry) != 0
                    && !FieldAttribute.IsProtected(_p.Buffer[a])
                    && (_p.Buffer[a] & FieldAttribute.Mdt) == 0)
                {
                    return a;
                }
            }

            return -1;
        }

        private bool TypeByte(byte ebcdic, bool tabAfter)
        {
            if (!CheckInputPosition(out int fa))
            {
                return false;
            }

            if (InsertMode && _p.Buffer[CursorAddress] != FormatControl.Null)
            {
                // Insert (GA23-0061-2 p. C-5): the first null at or after the cursor in the field
                // takes the shift; characters after that null stay. No null: Too Much Data. The
                // operator guide's "one less than the blanks" (GA27-2890-4 p. 3-42) is a rule of
                // thumb for the operator; the control unit's rule is the null count, used here.
                int size = Size;
                int hole = -1;
                int p = CursorAddress;
                for (int n = 0; n < size && !_p.IsFieldAttributeAt[p]; n++)
                {
                    if (_p.Buffer[p] == FormatControl.Null)
                    {
                        hole = p;
                        break;
                    }

                    p = Next(p);
                }

                if (hole < 0)
                {
                    _deviceLock = KeyboardLock.TooMuchData;
                    return false;
                }

                // Shift right, the last row's character moving to the next row; character
                // attributes move with the characters (p. C-5).
                while (hole != CursorAddress)
                {
                    int from = hole == 0 ? size - 1 : hole - 1;
                    MoveCell(from, hole);
                    hole = from;
                }
            }

            Prime(fa);

            // Numeric fields: with a plain typewriter keyboard the 3274 accepts any character
            // (GA23-0061-2 Figure 6-2, KEYBOARD.md 4.4), so no check is made here.
            SetMdt(fa);

            // The character attributes of a typed position are X'00' (p. C-3).
            StoreCharacter(CursorAddress, ebcdic, graphicEscape: false, useSa: false);
            if (tabAfter)
            {
                // DUP: "a Tab key operation" (p. C-6). On an unformatted screen Tab gives 0.
                OperatorMove(NextInputPosition(CursorAddress));
                return true;
            }

            int next = Next(CursorAddress);

            // Never leave the cursor on a field attribute: skip an autoskip field to the next input
            // field, otherwise step past the attribute. The IBM rule in KEYBOARD.md 4.2 covers only
            // the autoskip case; the step past a plain attribute follows x3270 kybd.c
            // ("Implement auto-skip"), read, not copied.
            int guard = Size;
            while (_p.IsFieldAttributeAt[next] && guard-- > 0)
            {
                next = FieldAttribute.IsAutoSkip(_p.Buffer[next]) ? NextInputPosition(next) : Next(next);
            }

            OperatorMove(next);
            return true;
        }

        private bool CheckInputPosition(out int fieldAttribute)
        {
            // Keys that change data need an alphanumeric position in an unprotected field; on a
            // field attribute or in a protected field they lock the keyboard ("Go Elsewhere",
            // GA27-2890-4 p. 2-21; GA23-0061-2 p. C-3 to C-7). An unformatted screen is all input.
            fieldAttribute = GetFieldAttributeFor(CursorAddress);
            if (fieldAttribute >= 0 && (_p.IsFieldAttributeAt[CursorAddress] || FieldAttribute.IsProtected(_p.Buffer[fieldAttribute])))
            {
                _deviceLock = KeyboardLock.OperatorError;
                return false;
            }

            return true;
        }

        private void SetMdt(int fieldAttribute)
        {
            if (fieldAttribute >= 0)
            {
                _p.Buffer[fieldAttribute] = (byte)(_p.Buffer[fieldAttribute] | FieldAttribute.Mdt);
            }
        }

        private void MoveCell(int from, int to)
        {
            // A character and its character attributes, never a field attribute.
            _p.Buffer[to] = _p.Buffer[from];
            _p.GraphicEscapeAt[to] = _p.GraphicEscapeAt[from];
            _p.CharHighlight[to] = _p.CharHighlight[from];
            _p.CharColor[to] = _p.CharColor[from];
            _p.CharCharset[to] = _p.CharCharset[from];
            _p.CharBackground[to] = _p.CharBackground[from];
            _p.CharTransparency[to] = _p.CharTransparency[from];
        }

        private void Write(ReadOnlySpan<byte> data, bool erase, bool alternate, bool fromStructuredField = false)
        {
            // "A write operation to that partition" unprimes a mandatory-fill field (p. 4-21) and
            // a trigger field (p. 4-23).
            _p.PrimedField = -1;
            _p.PrimedTrigger = -1;
            _insertCursorSeen = false;

            // "A write command with no WCC and no data is a no-op" (p. 3-2); an EW with no WCC is a
            // no-op too, nothing is erased (p. 3-6).
            if (data.Length == 0)
            {
                return;
            }

            byte wcc = data[0];
            if (fromStructuredField && !erase)
            {
                // A Write inside Outbound 3270DS ignores WCC bits 0-3 (p. 5-47).
                wcc = (byte)(wcc & 0x0F);
            }

            if (erase)
            {
                // Erase/Write steps (p. 3-6): screen size, nulls, attributes to X'00', cursor to 0.
                // Inside Outbound 3270DS the size does not change (p. 5-47). Nor does it in explicit
                // partitioned state: step 1 sets the size only "If in implicit partition state"
                // (p. 3-6), so an EW to explicit partition 0 erases it at its own size.
                if (fromStructuredField || _explicitState)
                {
                    Resize(Rows, Columns);
                }
                else if (alternate)
                {
                    Resize(_sessionAlternateRows, _sessionAlternateColumns);
                }
                else
                {
                    Resize(_sessionRows, _sessionColumns);
                }

                CursorAddress = 0;

                if ((wcc & Wcc.Reset) != 0)
                {
                    // EW or EWA with the WCC reset bit puts the reply mode back to Field (p. 5-62).
                    _p.InboundReplyMode = ReplyMode.Field;
                    _p.ReplyTypes = Array.Empty<byte>();
                }
            }
            if ((wcc & Wcc.ResetMdt) != 0)
            {
                // Reset MDT happens before any data or order (p. 3-3).
                for (int i = 0; i < _p.Buffer.Length; i++)
                {
                    if (_p.IsFieldAttributeAt[i])
                    {
                        _p.Buffer[i] = (byte)(_p.Buffer[i] & ~FieldAttribute.Mdt);
                    }
                }
            }

            // The current SA set starts at default on every write command (p. 4-5).
            _saHighlight = _saColor = _saCharset = _saBackground = _saTransparency = 0;

            // Write starts at the cursor when no SBA follows the WCC (p. 3-5); EW has put it at 0.
            int address = CursorAddress;
            bool afterCommandOrOrder = true;
            bool ptNullingWrapped = false;

            int i2 = 1;
            while (i2 < data.Length)
            {
                byte b = data[i2++];
                bool wasPt = false;
                switch (b)
                {
                    case Order3270.StartField:
                        if (i2 >= data.Length)
                        {
                            Error("SF without its attribute byte at the end of the record (p. 4-1).");
                            goto Done;
                        }

                        SetFieldAttribute(address, (byte)(data[i2++] & FieldAttribute.Meaningful));
                        address = Next(address);
                        break;

                    case Order3270.StartFieldExtended:
                    {
                        if (!ReadPairs(data, ref i2, out int start, out int count))
                        {
                            goto Done;
                        }

                        // "Unspecified types take their default" (p. 4-2); the field attribute is the
                        // type X'C0' pair, so a missing one gives the default attribute X'00'
                        // (Table 4-2, p. 4-28).
                        SetFieldAttribute(address, 0);
                        if (!ApplyFieldPairs(address, data.Slice(start, count * 2), fromSfe: true))
                        {
                            goto Done;
                        }

                        address = Next(address);
                        break;
                    }

                    case Order3270.ModifyField:
                    {
                        if (!ReadPairs(data, ref i2, out int start, out int count))
                        {
                            goto Done;
                        }

                        if (!_p.IsFieldAttributeAt[address])
                        {
                            Error("MF where the buffer address holds no field attribute is rejected (p. 4-6).");
                            goto Done;
                        }

                        if (!ApplyFieldPairs(address, data.Slice(start, count * 2), fromSfe: false))
                        {
                            goto Done;
                        }

                        address = Next(address);
                        break;
                    }

                    case Order3270.SetBufferAddress:
                        if (!ReadAddress(data, ref i2, out int sba))
                        {
                            goto Done;
                        }

                        address = sba;
                        break;

                    case Order3270.SetAttribute:
                        if (i2 + 1 >= data.Length)
                        {
                            Error("SA cut short by the end of the record (p. 4-1).");
                            goto Done;
                        }

                        if (!ApplySetAttribute(data[i2], data[i2 + 1]))
                        {
                            goto Done;
                        }

                        i2 += 2;
                        break;

                    case Order3270.InsertCursor:
                        CursorAddress = address;
                        _insertCursorSeen = true;
                        break;

                    case Order3270.ProgramTab:
                        address = ProgramTab(address, !afterCommandOrOrder || ptNullingWrapped, out ptNullingWrapped);
                        wasPt = true;
                        break;

                    case Order3270.RepeatToAddress:
                    {
                        if (!ReadAddress(data, ref i2, out int stop))
                        {
                            goto Done;
                        }

                        bool ge = false;
                        if (i2 < data.Length && data[i2] == Order3270.GraphicEscape)
                        {
                            ge = true;
                            i2++;
                        }

                        if (i2 >= data.Length)
                        {
                            Error("RA without its character at the end of the record (p. 4-8).");
                            goto Done;
                        }

                        byte c = data[i2++];
                        int a = address;
                        do
                        {
                            // Field attributes in the range are overwritten (p. 4-8).
                            StoreCharacter(a, c, ge, useSa: true);
                            a = Next(a);
                        }
                        while (a != stop);

                        address = stop;
                        break;
                    }

                    case Order3270.EraseUnprotectedToAddress:
                    {
                        if (!ReadAddress(data, ref i2, out int stop))
                        {
                            goto Done;
                        }

                        int fa = GetFieldAttributeFor(address);
                        bool isProtected = fa >= 0 && FieldAttribute.IsProtected(_p.Buffer[fa]);
                        int a = address;
                        do
                        {
                            if (_p.IsFieldAttributeAt[a])
                            {
                                // Field attributes are left alone; they decide what follows (p. 4-9).
                                isProtected = FieldAttribute.IsProtected(_p.Buffer[a]);
                            }
                            else if (!isProtected)
                            {
                                StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                            }

                            a = Next(a);
                        }
                        while (a != stop);

                        address = stop;
                        break;
                    }

                    case Order3270.GraphicEscape:
                        if (i2 >= data.Length)
                        {
                            Error("GE without its character at the end of the record (p. 4-9).");
                            goto Done;
                        }

                        byte g = data[i2++];
                        if (g < 0x40 || g == 0xFF)
                        {
                            Error("GE with a character outside X'40'-X'FE' is rejected (p. 4-9).");
                            goto Done;
                        }

                        StoreCharacter(address, g, graphicEscape: true, useSa: true);
                        address = Next(address);
                        afterCommandOrOrder = false;
                        ptNullingWrapped = false;
                        continue;

                    default:
                        if (b < 0x40 && !FormatControl.IsStorable(b))
                        {
                            Error("Byte X'" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' is in the order range but is no order and no stored control (p. 4-1).");
                            goto Done;
                        }

                        StoreCharacter(address, b, graphicEscape: false, useSa: true);
                        address = Next(address);
                        afterCommandOrOrder = false;
                        ptNullingWrapped = false;
                        continue;
                }

                afterCommandOrOrder = true;
                if (!wasPt)
                {
                    ptNullingWrapped = false;
                }
            }

        Done:
            // Every WCC function except Reset MDT waits until the data and orders are done (p. 3-2).
            // Keyboard restore ends the display's conditions and this partition's System Lock
            // (p. 2-9: "a write operation with keyboard restore to that partition"). PWAIT ends
            // with the acknowledgment the caller makes (AcknowledgeAfterWrite, or the WSF case),
            // which a write without keyboard restore makes as well, leaving System Lock shown
            // (see KeyboardLock.SystemLock).
            if ((wcc & Wcc.KeyboardRestore) != 0)
            {
                _deviceLock = KeyboardLock.None;
                _p.SysLock = false;
                _lastAid = Aid.None;
            }

            if ((wcc & Wcc.SoundAlarm) != 0)
            {
                Alarm?.Invoke();
            }
        }

        private void WriteStructuredField(ReadOnlySpan<byte> data)
        {
            // First pass: check every field's length, that Read Partition is the last one, and the
            // file transfer total, before anything is applied. Each field is a 2-byte length that
            // counts itself, then the ID; length 0 means the field runs to the end (p. 5-1).
            int fileTransferBytes = 0;
            bool holdsReadPartition = false;
            int pos = 0;
            while (pos < data.Length)
            {
                if (!NextStructuredField(data, pos, out int length))
                {
                    return;
                }

                byte id = data[pos + 2];
                if (id == StructuredFieldId.ReadPartition && pos + length < data.Length)
                {
                    Error("Read Partition must be the last structured field in the transmission (p. 5-48).");
                    return;
                }

                if (id == StructuredFieldId.DataTransfer)
                {
                    fileTransferBytes += length;
                }

                if (id == StructuredFieldId.ReadPartition)
                {
                    holdsReadPartition = true;
                }

                pos += length;
            }

            // The WSF acknowledges the outstanding inbound operation (p. 3-23), unless it carries
            // a Read Partition, which is a read - and is rejected in a retry state anyway
            // (p. 3-18) - while INOP is a read. A query is acknowledged by any WSF (p. 3-23,
            // 10-5). An affirmative trigger reply is acknowledged below, after its Outbound 3270DS.
            if (_triggerReply != TriggerReply.Affirmative && (!holdsReadPartition || _inop == InopQuery))
            {
                AcknowledgeInbound();
            }

            if (fileTransferBytes > 0)
            {
                if (_fileTransferLimit == 0)
                {
                    Error("A file transfer structured field (X'D0') arrived, but file transfer is off, so the DDM query reply was never sent.");
                    return;
                }

                if (fileTransferBytes > _fileTransferLimit)
                {
                    Error("The file transfer fields add up to " + fileTransferBytes + " bytes, more than the " + _fileTransferLimit + " this terminal reported as LIMOUT (p. 6-37).");
                    return;
                }
            }

            pos = 0;
            while (pos < data.Length)
            {
                NextStructuredField(data, pos, out int length);
                ReadOnlySpan<byte> field = data.Slice(pos, length);
                pos += length;

                bool ok;
                switch (field[2])
                {
                    case StructuredFieldId.ReadPartition:
                        ok = ReadPartition(field);
                        break;
                    case StructuredFieldId.EraseReset:
                        ok = EraseReset(field);
                        break;
                    case StructuredFieldId.SetReplyMode:
                        ok = SetReplyMode(field);
                        break;
                    case StructuredFieldId.LoadProgrammedSymbols:
                    {
                        // p. 5-18 to 5-33. The slot size is SDW x SDH of the Character Sets reply
                        // (MS is 0), and the sets are triple-plane on a colour screen.
                        string? reject = ProgrammedSymbols.Load(field, CellWidth, CellHeight, Color);
                        ok = reject == null;
                        if (reject != null)
                        {
                            Error(reject);
                        }

                        break;
                    }

                    case StructuredFieldId.Outbound3270DS:
                        ok = Outbound3270DS(field);
                        if (ok && _triggerReply == TriggerReply.Affirmative)
                        {
                            // The first structured field was the Write with keyboard restore to
                            // INPID: the affirmative reply (p. 4-24). It acknowledges the trigger
                            // action, then the queued keystrokes run on its result.
                            _triggerReply = TriggerReply.None;
                            AcknowledgeInbound();
                            TriggerAffirmative();
                        }

                        break;
                    case StructuredFieldId.DataTransfer:
                        FileTransferField?.Invoke(field);
                        ok = true;
                        break;
                    case CreatePartitionId:
                        ok = CreatePartition(field);
                        break;
                    case SetWindowOriginId:
                        ok = SetWindowOrigin(field);
                        break;
                    case DestroyPartitionId:
                        ok = DestroyPartition(field);
                        break;
                    case ActivatePartitionId:
                        ok = ActivatePartition(field);
                        break;
                    case ResetPartitionId:
                        ok = ResetPartition(field);
                        break;
                    default:
                        Error("Structured field X'" + Hex(field[2]) + "' is not built (only Read Partition, Erase/Reset, Load Programmed Symbols, Set Reply Mode, Outbound 3270DS, Create, Destroy, Activate Reset and Set Window Origin, and X'D0' file transfer are).");
                        ok = false;
                        break;
                }

                if (!ok)
                {
                    // A rejected field stops the rest of the transmission (p. 5-1).
                    return;
                }
            }
        }

        private bool NextStructuredField(ReadOnlySpan<byte> data, int pos, out int length)
        {
            length = 0;
            if (pos + 3 > data.Length)
            {
                Error("Structured field cut short: fewer than 3 bytes left for its length and ID (p. 5-1).");
                return false;
            }

            length = (data[pos] << 8) | data[pos + 1];
            if (length == 0)
            {
                length = data.Length - pos;
            }

            if (length < 3 || pos + length > data.Length)
            {
                Error("Structured field length " + length + " does not fit the " + (data.Length - pos) + " bytes left (p. 5-1).");
                return false;
            }

            byte id = data[pos + 2];
            if (id == 0x0F && pos + 3 < data.Length && data[pos + 3] == 0x0A)
            {
                // Modify Partition, X'0F0A' (p. 5-34, 5-35): optional, and this display's
                // Alphanumeric Partitions reply has MODIFY 0 (p. 6-19), so a host must not send it.
                Error("Modify Partition (X'0F0A') is not supported: the Alphanumeric Partitions query reply has MODIFY 0 (p. 5-34, 6-19).");
                return false;
            }

            if (id == 0x0F || id == 0x10)
            {
                // Two-byte IDs begin with X'0F' or X'10' (p. 5-2); none of them is built.
                Error("Structured field with the two-byte ID X'" + Hex(id) + (length > 3 ? Hex(data[pos + 3]) : string.Empty) + "' is not built.");
                return false;
            }

            return true;
        }

        private bool ReadPartition(ReadOnlySpan<byte> field)
        {
            // L, X'01', PID, TYPE, then for Query List the flags byte and the codes (p. 5-47).
            if (field.Length < 5)
            {
                Error("Read Partition shorter than 5 bytes (p. 5-47).");
                return false;
            }

            byte pid = field[3];
            byte type = field[4];
            if (_readState != ReadState.Normal)
            {
                // "If the device is in retry state, the Read Partition structured field is rejected
                // with sense code X'0871'" (p. 3-18; Table B-1 p. B-3: read partition, state
                // error).
                Error("Read Partition while an inbound operation is not yet acknowledged (retry state) is rejected, sense X'0871' (p. 3-18).");
                return false;
            }

            switch (type)
            {
                case 0x02:
                    if (pid != 0xFF)
                    {
                        Error("Read Partition Query with partition X'" + Hex(pid) + "', which must be X'FF' (p. 5-48).");
                        return false;
                    }

                    SendQueryReplies(Array.Empty<byte>(), all: true);
                    EnterRetryRead(InopQuery);
                    return true;

                case 0x03:
                {
                    if (pid != 0xFF)
                    {
                        Error("Read Partition Query List with partition X'" + Hex(pid) + "', which must be X'FF' (p. 5-48).");
                        return false;
                    }

                    if (field.Length < 6)
                    {
                        Error("Read Partition Query List without its request type byte (p. 5-47).");
                        return false;
                    }

                    // Bits 0-1 of byte 5, IBM bit 0 being the high bit (p. 5-47, 5-48).
                    int requestType = field[5] >> 6;
                    ReadOnlySpan<byte> codes = field.Slice(6);
                    switch (requestType)
                    {
                        case 0:
                            SendQueryReplies(codes, all: false);
                            EnterRetryRead(InopQuery);
                            return true;
                        case 1:
                            // "Equivalent": everything a Query sends, plus the listed codes, no
                            // duplicates. A Query sends every reply this terminal has, so the list
                            // adds nothing.
                            SendQueryReplies(codes, all: true);
                            EnterRetryRead(InopQuery);
                            return true;
                        case 2:
                            // "All" overrides the list.
                            SendQueryReplies(Array.Empty<byte>(), all: true);
                            EnterRetryRead(InopQuery);
                            return true;
                        default:
                            Error("Read Partition Query List with the reserved request type B'11' (p. 5-47).");
                            return false;
                    }
                }

                case Command3270.ReadBuffer:
                case Command3270.ReadModified:
                case Command3270.ReadModifiedAll:
                {
                    // "The operation type is Read Modified, Read Modified All, or Read Buffer, and
                    // PID does not exist" is rejected (p. 5-48).
                    Partition3270? target = PartitionFor(pid);
                    if (target == null)
                    {
                        Error("Read Partition of partition X'" + Hex(pid) + "', which does not exist, is rejected (p. 5-48; Table B-1 p. B-2, PID is unknown partition).");
                        return false;
                    }

                    // p. 5-49 steps 2 to 8: INPID is set to the PID, INOP to the operation, the
                    // data goes inbound and the device is in retry-read state, where a Read
                    // Modified command sends the same data again (p. 3-17, 3-18).
                    _inboundPid = pid;
                    _p = target;

                    // A partition with a nonzero PID answers in an Inbound 3270DS with AID X'61'
                    // (p. 3-16, 6-7 case 2, 5-49 step 8). Partition 0 answers in plain 3270 format
                    // (p. 6-7, 3-16), and there the manual contradicts itself on the AID: p. 5-49
                    // step 8 says X'61', but p. 3-9 note 2 says X'61' can never be the first byte
                    // of an inbound stream, and in plain 3270 format the AID is the first byte. The
                    // current AID is sent for partition 0, which obeys p. 3-9.
                    byte aid = pid == 0 ? _lastAid : Aid.ReadPartition;
                    if (type == Command3270.ReadBuffer)
                    {
                        SendReadBuffer(aid);
                    }
                    else
                    {
                        SendReadModified(aid, all: type == Command3270.ReadModifiedAll);
                    }

                    EnterRetryRead(type);
                    SelectActive();
                    return true;
                }

                default:
                    Error("Read Partition type X'" + Hex(type) + "' is reserved and rejected (p. 5-48).");
                    return false;
            }
        }

        // After a Read Partition's reply (p. 5-48, 5-49): INOP is the operation, the reply is kept,
        // and the device is in retry-read state until the host acknowledges (p. 3-17, 3-18). A
        // query's INPID is not set by the Read Partition (p. 3-19: "For Query operations, INPID is
        // not used"); it stays as it was, and no partition has PWAIT for it.
        private void EnterRetryRead(byte inop)
        {
            _inop = inop;
            _readState = ReadState.RetryRead;
            KeepLastInboundForRetry();
        }

        private bool EraseReset(ReadOnlySpan<byte> field)
        {
            // L, X'03', flags; bit 0 (X'80') IPZ: the implicit partition gets the alternate size,
            // otherwise the default size (p. 5-15). All partitions are destroyed and implicit
            // partition 0 is made; with no partitions built that is the screen itself.
            if (field.Length < 4)
            {
                Error("Erase/Reset without its flags byte (p. 5-15).");
                return false;
            }

            // "Resets the device to implicit partition state destroying all existing (implicit or
            // explicit) partitions" and makes implicit partition 0 "with default partition
            // characteristics" (p. 5-15): Field reply mode, INPID 0.
            if ((field[3] & 0x80) != 0)
            {
                ResetToImplicit(_sessionAlternateRows, _sessionAlternateColumns);
            }
            else
            {
                ResetToImplicit(_sessionRows, _sessionColumns);
            }

            return true;
        }

        private bool SetReplyMode(ReadOnlySpan<byte> field)
        {
            // L, X'09', PID, MODE, then the attribute type list for Character mode (p. 5-61).
            if (field.Length < 5)
            {
                Error("Set Reply Mode shorter than 5 bytes (p. 5-61).");
                return false;
            }

            // The mode belongs to the partition named (p. 5-61); "PID is unknown partition" is an
            // error (Table B-1, p. B-2).
            Partition3270? target = PartitionFor(field[3]);
            if (target == null)
            {
                Error("Set Reply Mode for partition X'" + Hex(field[3]) + "', which does not exist, is rejected (Table B-1 p. B-2, PID is unknown partition).");
                return false;
            }

            byte mode = field[4];
            if (mode > (byte)ReplyMode.Character)
            {
                Error("Set Reply Mode X'" + Hex(mode) + "' is reserved and rejected (p. 5-62).");
                return false;
            }

            ReadOnlySpan<byte> types = mode == (byte)ReplyMode.Character ? field.Slice(5) : ReadOnlySpan<byte>.Empty;
            for (int i = 0; i < types.Length; i++)
            {
                if (Array.IndexOf(CharacterAttributeTypes, types[i]) < 0)
                {
                    Error("Set Reply Mode lists attribute type X'" + Hex(types[i]) + "', which is not a character attribute type (p. 5-62).");
                    return false;
                }
            }

            target.InboundReplyMode = (ReplyMode)mode;
            target.ReplyTypes = types.ToArray();
            return true;
        }

        private bool Outbound3270DS(ReadOnlySpan<byte> field)
        {
            // L, X'40', PID, CMND, then the WCC (none for EAU) and the orders and data (p. 5-40).
            if (field.Length < 5)
            {
                Error("Outbound 3270DS shorter than 5 bytes (p. 5-40).");
                return false;
            }

            // "The specified partition must be defined or a data stream error occurs" (p. 5-40);
            // in implicit partition state only PID 0 is (Table 3-3, p. 3-4: "If the PID does not
            // equal 0, reject with -RSP"). The orders and data are then the partition's: buffer
            // addresses relative to its origin, in its address mode (p. 4-3, 4-4), and a WCC reset
            // resets only that partition (p. 5-41).
            Partition3270? target = PartitionFor(field[3]);
            if (target == null)
            {
                Error("Outbound 3270DS for partition X'" + Hex(field[3]) + "', which does not exist, is rejected (p. 5-40; Table 3-3 p. 3-4).");
                return false;
            }

            _p = target;
            bool ok = Outbound3270DSCommand(field);
            SelectActive();
            return ok;
        }

        private bool Outbound3270DSCommand(ReadOnlySpan<byte> field)
        {
            ReadOnlySpan<byte> rest = field.Slice(5);
            switch (field[4])
            {
                case Command3270.Write:
                    Write(rest, erase: false, alternate: false, fromStructuredField: true);
                    return true;
                case Command3270.EraseWrite:
                    Write(rest, erase: true, alternate: false, fromStructuredField: true);
                    return true;
                case Command3270.EraseWriteAlternate:
                    Write(rest, erase: true, alternate: true, fromStructuredField: true);
                    return true;
                case Command3270.EraseAllUnprotected:
                    EraseAllUnprotected();
                    return true;
                case 0xF7:
                    Error("Outbound 3270DS with BSC Copy (X'F7') is not built: it needs remote BSC, which TN3270 is not (p. 5-41).");
                    return false;
                default:
                    Error("Outbound 3270DS command X'" + Hex(field[4]) + "' is reserved and rejected (p. 5-40).");
                    return false;
            }
        }

        // Create Partition, X'0C' (GA23-0059-4 p. 5-10 to 5-13). Bytes: 0-1 L, 2 X'0C', 3 PID,
        // 4 UOM (bits 0-3) and A-MODE (bits 4-7), 5 flags (bit 0 reserved, 1 PROT, 2 COPY, 3-4
        // reserved, 5-7 BASE), 6-7 H, 8-9 W, 10-11 RV, 12-13 CV, 14-15 HV, 16-17 WV, 18-19 RW,
        // 20-21 CW, 22-23 RS, 24-25 reserved, 26-27 PW, 28-29 PH. "Working backwards from byte 29,
        // the above parameters may be progressively omitted by specifying the appropriate length"
        // (p. 5-12), with the defaults of the table on p. 5-12.
        //
        // What is built: character-cell units only, 12/14-bit or 16-bit addressing, and a
        // presentation space the same size as its viewport with the window at (0, 0) - no
        // scrolling, which the Alphanumeric Partitions reply says (SCROLL = 0, p. 6-19). Anything
        // else this display reports as not supported is refused out loud, never approximated.
        private bool CreatePartition(ReadOnlySpan<byte> field)
        {
            int length = field.Length;
            if (length > 30 || (length > 6 && (length & 1) != 0))
            {
                // A two-byte parameter cut in half, or more than byte 29.
                Error("Create Partition of " + length + " bytes: its parameters end at byte 29 and are left off two bytes at a time from the end (p. 5-11, 5-12; Table B-1 p. B-2, Incorrect length).");
                return false;
            }

            byte pid = length > 3 ? field[3] : (byte)0;
            if (pid >= MaxPartitions)
            {
                Error("Create Partition PID X'" + Hex(pid) + "' is above the highest this display takes, X'" + Hex(MaxPartitions - 1) + "' (Alphanumeric Partitions NA, p. 6-18; Table B-1 p. B-2, PID > maximum allowed).");
                return false;
            }

            byte modes = length > 4 ? field[4] : (byte)0;
            int uom = modes >> 4;
            int addressMode = modes & 0x0F;
            if (uom != 0)
            {
                // "If the CELL parameter of the Usable Area Query Reply is B'0', only UOM =
                // B'0000' is supported" (p. 5-12); this display's CELLUNITS is 0.
                Error("Create Partition UOM B'" + Convert.ToString(uom, 2).PadLeft(4, '0') + "': only character cells (B'0000') are supported, the Usable Area reply has CELLUNITS 0 (p. 5-12; Table B-1 p. B-2, Invalid UOM value).");
                return false;
            }

            if (addressMode == 2)
            {
                Error("Create Partition A-MODE B'0010' asks for a text partition, which is not built: no Text Partitions query reply is sent (p. 5-11, 5-13).");
                return false;
            }

            if (addressMode > 2)
            {
                Error("Create Partition A-MODE B'" + Convert.ToString(addressMode, 2).PadLeft(4, '0') + "' is reserved (p. 5-11; Table B-1 p. B-2, Invalid A-MODE).");
                return false;
            }

            byte flags = length > 5 ? field[5] : (byte)0;
            if ((flags & 0x98) != 0)
            {
                Error("Create Partition flags X'" + Hex(flags) + "' set reserved bit 0, 3 or 4 (p. 5-11; Table B-1 p. B-2, Reserved field is not zero).");
                return false;
            }

            if ((flags & 0x40) != 0)
            {
                Error("Create Partition asks for a protected partition (PROT), which this display does not support: its Alphanumeric Partitions reply has PROT 0 (p. 5-11, 6-19).");
                return false;
            }

            if ((flags & 0x20) != 0)
            {
                Error("Create Partition asks for a presentation space local copy (COPY), which this display does not support: its Alphanumeric Partitions reply has COPY 0 (p. 5-11, 6-19).");
                return false;
            }

            if ((flags & 0x07) != 0)
            {
                // BASE counts down the base set descriptors of the Character Sets reply (p. 6-25);
                // this display sends one, so only index 0 exists.
                Error("Create Partition BASE " + (flags & 0x07) + ": the Character Sets reply has one base character set, index 0 (p. 5-10, 6-25; Table B-1 p. B-2, Base > supported number).");
                return false;
            }

            // The usable area the viewport goes on. In implicit partition state, or when this
            // Create replaces the only partition, it is chosen now: "the device first tries to fit
            // the viewport in the base usable area, then in the alternate one, and rejects the
            // structured field if it fits in neither" (p. 6-93); the alternate one is "limited to a
            // single-partition implementation" (p. 6-93). Otherwise it is the one in use.
            UsableArea(out int baseRows, out int baseColumns, out bool nested);
            int screenRows = _usableRows;
            int screenColumns = _usableColumns;
            bool onlyPartition = !_explicitState || (CountPartitions() == 1 && _partitions[pid] != null);
            if (onlyPartition)
            {
                screenRows = baseRows;
                screenColumns = baseColumns;
            }

            int h = Field16(field, 6, _sessionRows);
            int w = Field16(field, 8, _sessionColumns);
            int rv = Field16(field, 10, 0);
            int cv = Field16(field, 12, 0);
            int hv = Field16(field, 14, Math.Min(h, screenRows));
            int wv = Field16(field, 16, Math.Min(w, screenColumns));
            if (onlyPartition && !nested && (rv + hv > screenRows || cv + wv > screenColumns)
                && rv + hv <= _alternateRows && cv + wv <= _alternateColumns)
            {
                screenRows = _alternateRows;
                screenColumns = _alternateColumns;
                if (length < 16)
                {
                    // The viewport defaults follow the usable area it now goes on.
                    hv = Field16(field, 14, Math.Min(h, screenRows));
                    wv = Field16(field, 16, Math.Min(w, screenColumns));
                }
            }

            int rw = Field16(field, 18, 0);
            int cw = Field16(field, 20, 0);
            int rs = Field16(field, 22, h != hv ? 1 : 0);
            int cs = Field16(field, 24, w != wv ? 1 : 0);
            int pw = Field16(field, 26, CellWidth);
            int ph = Field16(field, 28, CellHeight);

            if (hv == 0 || wv == 0)
            {
                Error("Create Partition with a viewport " + hv + " high and " + wv + " wide: a zero viewport needs Modify Partition, which this display does not support (p. 5-10 conditions 1 and 2, 6-19).");
                return false;
            }

            if (rv + hv > screenRows || cv + wv > screenColumns)
            {
                Error("Create Partition viewport at row " + rv + ", column " + cv + ", " + hv + " x " + wv + ", does not fit the " + screenRows + " x " + screenColumns + " usable area (p. 5-10 conditions 3, 4 and 6; Table B-1 p. B-2, Specification error in Viewport/Window).");
                return false;
            }

            for (int i = 0; i < _partitions.Length; i++)
            {
                Partition3270? other = _partitions[i];
                if (!_explicitState || other == null || i == pid)
                {
                    // The partition with this PID is destroyed and replaced (p. 5-10).
                    continue;
                }

                bool apart = rv >= other.ViewportRow + other.ViewportRows || rv + hv <= other.ViewportRow
                    || cv >= other.ViewportColumn + other.ViewportColumns || cv + wv <= other.ViewportColumn;
                if (!apart)
                {
                    Error("Create Partition viewport at row " + rv + ", column " + cv + ", " + hv + " x " + wv + ", overlaps the viewport of partition " + i + " (p. 5-10 condition 5; Table B-1 p. B-2, Specification error in Viewport/Window).");
                    return false;
                }
            }

            // RS, the rows a scroll key moves (p. 5-11, 7-16): any value is taken. 0 means the
            // scroll keys move nothing, X'FFFF' "the device determines the amount" (p. 5-12),
            // here one viewport height - both this display's choices.
            if (cs >= 0x0002 && cs <= 0xFFFE)
            {
                // Bytes 24-25 are printed as reserved in the format table, and as CS in the
                // defaults table (p. 5-11, 5-12) and in Table B-1. Either reading rejects these.
                Error("Create Partition bytes 24-25 (CS) " + cs + ": horizontal windowing is not supported (Table B-1 p. B-2, CS = X'0002'-X'FFFE' and horizontal windowing not supported).");
                return false;
            }

            if (w != wv)
            {
                // Only vertical scrolling is built: the Alphanumeric Partitions reply sends the
                // SCROLL flag alone, and a wider presentation space than viewport would need
                // horizontal windowing (p. 2-3, 6-19; Table B-1 p. B-2).
                Error("Create Partition presentation space " + w + " columns wide in a viewport " + wv + " wide: horizontal windowing is not supported, so W must equal WV (p. 2-3, 6-19; Table B-1 p. B-2).");
                return false;
            }

            if (h < hv)
            {
                // "The window must be wholly contained within the presentation space" (p. 5-10
                // item 10), and the window is the viewport's size (p. 2-3).
                Error("Create Partition presentation space " + h + " rows high in a viewport " + hv + " high: the window cannot lie inside it (p. 2-3, 5-10 item 10; Table B-1 p. B-2, Specification error in Viewport/Window).");
                return false;
            }

            if (cw != 0)
            {
                Error("Create Partition window origin column " + cw + ": CW > 0, but horizontal windowing not supported (Table B-1 p. B-2).");
                return false;
            }

            if (rw + hv > h)
            {
                Error("Create Partition window origin row " + rw + " with a window " + hv + " rows high runs past the " + h + "-row presentation space (p. 5-10 item 10; Table B-1 p. B-2, Window spec outside presentation space).");
                return false;
            }

            if (pw != CellWidth || ph != CellHeight)
            {
                // VCP = 0 in the Usable Area reply: one cell size for every partition (p. 6-88).
                Error("Create Partition cell " + pw + " x " + ph + " points: this display has one cell size, " + CellWidth + " x " + CellHeight + " (Usable Area VCP 0, p. 6-88).");
                return false;
            }

            // Storage: CM(H x W) bytes per partition with CM = 1, the total at most M (p. 6-19).
            int used = h * w;
            for (int i = 0; _explicitState && i < _partitions.Length; i++)
            {
                Partition3270? other = _partitions[i];
                if (other != null && i != pid)
                {
                    used += other.Size;
                }
            }

            if (used > PartitionStorage)
            {
                Error("Create Partition needs " + used + " bytes of partition storage, more than the " + PartitionStorage + " this display has (p. 6-19; Table B-1 p. B-2, Insufficient resource).");
                return false;
            }

            if (addressMode == 0 && h * w > 0x4000)
            {
                // A 14-bit address reaches 16383 (p. 4-4); the manual gives no rule for a larger
                // 12/14-bit partition, so it is refused rather than half addressable.
                Error("Create Partition of " + (h * w) + " cells with 12/14-bit addressing: 14 bits reach only 16384 cells (p. 4-4); use A-MODE B'0001'.");
                return false;
            }

            // Everything is valid: make it. In implicit partition state "implicit partition 0 is
            // destroyed by the Create Partition and a partition identifier equal to PID is
            // created. The display state is changed ... to explicit partitioned state and the
            // created partition is made active" (p. 5-10). The destroyed implicit partition was
            // the inbound one, so INPID becomes 0 (p. 3-20).
            var created = new Partition3270(pid, h, w, rv, cv, hv, rw, rs, addressMode == 1);
            bool wasExplicit = _explicitState;
            Partition3270? replaced = wasExplicit ? _partitions[pid] : null;
            if (!wasExplicit)
            {
                // Implicit partition 0 was the inbound partition, and its destruction
                // acknowledges (p. 3-23).
                AcknowledgeInbound();
                Array.Clear(_partitions);
                _explicitState = true;
                _inboundPid = 0;
                _retryLength = 0;
            }
            else if (replaced != null && pid == _inboundPid)
            {
                // "Destruction of the inbound partition sets INPID to 0" (p. 3-20); a Create
                // Partition to the inbound partition is a read acknowledgment (p. 3-23).
                AcknowledgeInbound();
                _inboundPid = 0;
            }

            _usableRows = screenRows;
            _usableColumns = screenColumns;
            _partitions[pid] = created;

            // The cursor at the window origin and the buffer address 0 (p. 5-10 items 7 and 8):
            // the Partition3270 constructor puts the cursor at RW x W.
            if (!wasExplicit || CountPartitions() == 1)
            {
                // The first, or "the only partition with a window of nonzero extent" (p. 5-10).
                _activePid = pid;
            }

            // A replaced partition that was active stays the active one: the manual says only
            // that the old one is destroyed and replaced (p. 5-10), and names no other partition
            // to activate.
            SelectActive();
            return true;
        }

        // Set Window Origin, X'0B' (p. 5-63): X'0008' X'0B' PID RW(2) CW(2). The window of the
        // partition moves to row RW; a cursor outside the new window goes to the nearest row of
        // it (p. 5-63). CW must be 0, horizontal windowing is not built (Table B-1 p. B-5).
        private bool SetWindowOrigin(ReadOnlySpan<byte> field)
        {
            if (field.Length != 8)
            {
                Error("Set Window Origin is 8 bytes, X'0008' X'0B' PID RW CW (p. 5-63; Table B-1, Incorrect length).");
                return false;
            }

            byte pid = field[3];
            Partition3270? p = PartitionFor(pid);
            if (p == null)
            {
                Error("Set Window Origin of partition X'" + Hex(pid) + "', which does not exist, is rejected (p. 5-63; Table B-1, PID is unknown partition).");
                return false;
            }

            int rw = (field[4] << 8) | field[5];
            int cw = (field[6] << 8) | field[7];
            if (cw != 0)
            {
                Error("Set Window Origin CW " + cw + ": CW > 0, but horizontal windowing not supported (Table B-1 p. B-5).");
                return false;
            }

            if (rw + p.ViewportRows > p.Rows)
            {
                Error("Set Window Origin row " + rw + " with a window " + p.ViewportRows + " rows high runs past the " + p.Rows + "-row presentation space (p. 5-63; Table B-1, Window spec outside presentation space).");
                return false;
            }

            p.WindowRow = rw;
            int row = p.Cursor / p.Columns;
            int nearest = Math.Clamp(row, rw, rw + p.ViewportRows - 1);
            if (nearest != row)
            {
                // "the cursor is moved to the nearest row ... of the window"; the column stays.
                p.Cursor = (nearest * p.Columns) + (p.Cursor % p.Columns);
            }

            return true;
        }

        // Destroy Partition, X'0D' (p. 5-14).
        private bool DestroyPartition(ReadOnlySpan<byte> field)
        {
            if (field.Length != 4)
            {
                Error("Destroy Partition is 4 bytes, X'0004' X'0D' PID (p. 5-14; Table B-1 p. B-2, Incorrect length).");
                return false;
            }

            byte pid = field[3];
            if (pid >= MaxPartitions)
            {
                Error("Destroy Partition PID X'" + Hex(pid) + "' is above the highest this display takes, X'" + Hex(MaxPartitions - 1) + "' (Table B-1 p. B-2, PID > maximum allowed).");
                return false;
            }

            if (PartitionFor(pid) == null)
            {
                // "If the identified partition does not exist, Destroy Partition has no effect."
                return true;
            }

            if (!_explicitState || CountPartitions() == 1)
            {
                // The only existing partition (implicit partition 0 counts, p. 3-23 names "a
                // Destroy Partition structured field to the inbound partition including explicit
                // and implicit partition 0"): destroyed, implicit partition state with the default
                // implicit partition 0, "any Input Inhibit conditions" reset, INOP Read Modified
                // and INPID 0.
                ResetToImplicit(_sessionRows, _sessionColumns);
                ClearAllInputInhibit();
                return true;
            }

            if (pid == _activePid)
            {
                // "activates the next partition with a window of nonzero extent" - every
                // partition here has one - the next being the one with the smallest PID greater
                // than this one, or else the lowest (p. 2-5, the jump-partition rule).
                _activePid = NextPartitionAfter(pid);
            }

            if (pid == _inboundPid)
            {
                // A Destroy Partition to the inbound partition acknowledges (p. 3-23), and "sets
                // INPID to 0" (p. 3-20).
                AcknowledgeInbound();
                _inboundPid = 0;
            }

            _partitions[pid] = null;
            SelectActive();
            return true;
        }

        // Activate Partition, X'0E' (p. 5-8).
        private bool ActivatePartition(ReadOnlySpan<byte> field)
        {
            if (field.Length != 4)
            {
                Error("Activate Partition is 4 bytes, X'0004' X'0E' PID (p. 5-8; Table B-1 p. B-2, Incorrect length).");
                return false;
            }

            byte pid = field[3];
            if (PartitionFor(pid) == null)
            {
                Error("Activate Partition of partition X'" + Hex(pid) + "', which does not exist, is rejected (p. 5-8 step 1).");
                return false;
            }

            // The partition is made active and the screen cursor is shown at its current cursor
            // position (p. 5-8 steps 2 and 3). Its PWAIT and System Lock are its own
            // (Partition3270.Pwait, SysLock), so the old partition's are saved and the new one's
            // raised by making it active (steps 4 and 5a); the display's other conditions stay
            // shown (step 5b). Lock reads them in that order.
            _activePid = pid;
            SelectActive();
            return true;
        }

        // Reset Partition, X'00' (p. 5-51): the inbound reply mode back to Field. Format groups
        // and Set Partition Characteristics, the other two characteristics it resets, are not
        // built, so there is nothing else to reset.
        private bool ResetPartition(ReadOnlySpan<byte> field)
        {
            if (field.Length != 4)
            {
                Error("Reset Partition is 4 bytes, X'0004' X'00' PID (p. 5-51; Table B-1 p. B-2, Incorrect length).");
                return false;
            }

            Partition3270? target = PartitionFor(field[3]);
            if (target == null)
            {
                Error("Reset Partition of partition X'" + Hex(field[3]) + "', which does not exist, is rejected (Table B-1 p. B-2, PID is unknown partition).");
                return false;
            }

            target.InboundReplyMode = ReplyMode.Field;
            target.ReplyTypes = Array.Empty<byte>();
            return true;
        }

        private int CountPartitions()
        {
            int n = 0;
            for (int i = 0; i < _partitions.Length; i++)
            {
                if (_partitions[i] != null)
                {
                    n++;
                }
            }

            return n;
        }

        // The smallest PID greater than pid, or else the lowest PID (p. 2-5).
        private byte NextPartitionAfter(byte pid)
        {
            for (int i = pid + 1; i < _partitions.Length; i++)
            {
                if (_partitions[i] != null)
                {
                    return (byte)i;
                }
            }

            for (int i = 0; i < pid; i++)
            {
                if (_partitions[i] != null)
                {
                    return (byte)i;
                }
            }

            return pid;
        }

        // A two-byte parameter, or its default when the structured field ends before it.
        private static int Field16(ReadOnlySpan<byte> field, int offset, int defaultValue)
        {
            return field.Length >= offset + 2 ? (field[offset] << 8) | field[offset + 1] : defaultValue;
        }

        // The base usable area as the Usable Area reply reports it (see AppendQueryReply): the
        // larger size when one holds the other, else the default size with the alternate one in
        // an Alternate Usable Area parameter.
        private void UsableArea(out int rows, out int columns, out bool nested)
        {
            bool defaultHoldsAlternate = _defaultColumns >= _alternateColumns && _defaultRows >= _alternateRows;
            bool alternateHoldsDefault = _alternateColumns >= _defaultColumns && _alternateRows >= _defaultRows;
            nested = defaultHoldsAlternate || alternateHoldsDefault;
            columns = nested ? Math.Max(_defaultColumns, _alternateColumns) : _defaultColumns;
            rows = nested ? Math.Max(_defaultRows, _alternateRows) : _defaultRows;
        }

        // Keeps the inbound record just sent so that a Read Modified command sends it again: the
        // retry-read state of p. 3-17, 3-18 after a Read Partition read (p. 5-49 step 7).
        private void KeepLastInboundForRetry()
        {
            ReadOnlySpan<byte> record = _inbound.WrittenSpan;
            if (_retryRecord.Length < record.Length)
            {
                _retryRecord = new byte[record.Length];
            }

            record.CopyTo(_retryRecord);
            _retryLength = record.Length;
        }

        private void SendQueryReplies(ReadOnlySpan<byte> listed, bool all)
        {
            // AID X'88' once, then the replies in ascending code order, each code once however often
            // it is listed (p. 3-10, 5-48). Summary is among them for Query, Equivalent and All, and
            // for a list only when the list names it (p. 6-81).
            _inbound.Clear();
            AppendByte(Aid.StructuredField);

            bool any = false;
            ReadOnlySpan<byte> supported = SupportedQueryCodes();
            for (int i = 0; i < supported.Length; i++)
            {
                byte code = supported[i];
                if (all || listed.IndexOf(code) >= 0)
                {
                    AppendQueryReply(code);
                    any = true;
                }
            }

            if (!any)
            {
                // None of the listed codes is supported, or the list is empty: Null (p. 5-48, 6-67).
                AppendBytes(stackalloc byte[] { 0x00, 0x04, StructuredFieldId.QueryReply, QueryCode.Null });
            }

            Send();

            // The reply is out: retry-read state, TWAIT, until the host acknowledges (see
            // KeyboardLock.QueryReplySent for the pages). A display condition already up (an
            // operator error and the like) stays shown for its own reason. An enter cannot be
            // outstanding here: a Read Partition in a retry state is rejected (p. 3-18).
            if (_deviceLock == KeyboardLock.None)
            {
                _deviceLock = KeyboardLock.QueryReplySent;
            }
        }

        private ReadOnlySpan<byte> SupportedQueryCodes()
        {
            // Ascending order, the order they are sent in. DDM only while file transfer is on.
            return _fileTransferLimit > 0 ? QueryCodesWithFileTransfer : QueryCodesWithoutFileTransfer;
        }

        private void AppendQueryReply(byte code)
        {
            int start = _inbound.WrittenCount;
            AppendBytes(stackalloc byte[] { 0x00, 0x00, StructuredFieldId.QueryReply, code });
            switch (code)
            {
                case QueryCode.Summary:
                    // The codes of every reply this terminal has (p. 6-81).
                    AppendBytes(SupportedQueryCodes());
                    break;

                case QueryCode.UsableArea:
                {
                    // p. 6-85 to 6-90. Flags: ADDR X'3', "12/14/16-bit addressing allowed. This
                    // value is used to support partitions that use the 16-bit addressing" (p. 6-88),
                    // which Create Partition A-MODE B'0001' gives; no special features. Byte 5 is 0:
                    // VCP 0 (one cell size for every partition), matrix characters, CELLUNITS 0.
                    // W and H are the largest screen; units millimetres (X'01') with Xr and Yr from
                    // DotDistanceX and DotDistanceY. Until Load PS was built this sent inches with
                    // X'FFFFFFFF', "cannot be determined", which p. 6-89 allows only without Load PS.
                    //
                    // BUFFSZ is 0: "It applies only to a device that does not support partitions.
                    // If the device supports partitions, the Alphanumeric Partitions Query Reply will
                    // define the buffer size, and the BUFFSZ parameter will be set to zero" (p. 6-90).
                    //
                    // When one size holds the other (every 3278 model: 24 x 80 inside the
                    // alternate), the larger is "the maximum number of characters that can be
                    // presented" (p. 6-89) and is the only size reported.
                    //
                    // When neither holds the other (43 x 80 with 27 x 132), the larger column count
                    // with the larger row count is a screen that cannot be shown. p. 6-93 gives that
                    // very device, a 3180: base 43 rows by 80 columns, and 27 by 132 in an Alternate
                    // Usable Area self-defining parameter, which "is always required when the
                    // device supports more than one usable area size on one display surface". So
                    // the base is the default size, BUFFSZ 0 (partitions, see above), then
                    // XMIN, YMIN, XMAX and YMAX as zeros - p. 6-87 lets them be left out only when
                    // no self-defining parameter follows - then the parameter (p. 6-94).
                    // Create Partition uses the same base and alternate areas (UsableArea).
                    UsableArea(out int rows, out int columns, out bool nested);
                    AppendBytes(stackalloc byte[] { 0x03, 0x00 });
                    AppendUInt16(columns);
                    AppendUInt16(rows);

                    // UNITS millimetres, then Xr and Yr. A device with Load PS may not send
                    // X'FFFFFFFF' (p. 6-89), so DotDistanceX and DotDistanceY are sent.
                    AppendByte(0x01);
                    AppendUInt32(DotDistanceX);
                    AppendUInt32(DotDistanceY);
                    AppendByte((byte)CellWidth);
                    AppendByte((byte)CellHeight);
                    AppendUInt16(0);
                    if (!nested)
                    {
                        // XMIN, YMIN, XMAX, YMAX (bytes 23 to 26, p. 6-87).
                        AppendBytes(stackalloc byte[] { 0x00, 0x00, 0x00, 0x00 });

                        // Alternate Usable Area (p. 6-94): length X'13', X'02', reserved X'00',
                        // AUAID X'01', WAUA, HAUA, units millimetres with the same Xr and Yr ("with
                        // Load PS, bytes 8 to 18 must be supplied", p. 6-93), AWAUA, AHAUA.
                        AppendBytes(stackalloc byte[] { 0x13, 0x02, 0x00, 0x01 });
                        AppendUInt16(_alternateColumns);
                        AppendUInt16(_alternateRows);
                        AppendByte(0x01);
                        AppendUInt32(DotDistanceX);
                        AppendUInt32(DotDistanceY);
                        AppendByte((byte)CellWidth);
                        AppendByte((byte)CellHeight);
                    }

                    break;
                }

                case QueryCode.AlphanumericPartitions:
                    // p. 6-18, 6-19. NA: partitions 0 to MaxPartitions - 1 may be created. M: the
                    // bytes of partition storage. Flags X'80': SCROLL, vertical scrolling (bit 0,
                    // which means Create Partition with H > HV, Set Window Origin, and the
                    // operator's scroll keys, p. 6-19); no all-points addressing, no partition
                    // protection, no presentation space local copy, no Modify Partition. The Buffer Allocation parameter is left off: "unless bytes
                    // 3-6 (RO, CO, FO) are all zeros and byte 2 (CM) = 1" - here they are, so a
                    // partition of H x W takes H x W bytes (p. 6-19).
                    AppendByte(MaxPartitions);
                    AppendUInt16(PartitionStorage);
                    AppendByte(0x80);
                    break;

                case QueryCode.FieldOutlining:
                    // p. 6-49, 6-50. Flags reserved 0; SEP 0, separation of underline and overline
                    // is for printers; VPOS and HPOS where the renderer draws the lines; HPOS0 and
                    // HPOS1 zero "where the device does not support the Separation". RetroIO keeps
                    // and returns the outlining attribute (GetFieldOutlining); drawing it is the
                    // renderer's.
                    AppendBytes(stackalloc byte[] { 0x00, 0x00 });
                    AppendByte((byte)OutlineVerticalPosition);
                    AppendByte((byte)OutlineHorizontalPosition);
                    AppendBytes(stackalloc byte[] { 0x00, 0x00 });
                    break;

                case QueryCode.CharacterSets:
                    // p. 6-21 to 6-28. Flags X'B2': ALT (Graphic Escape is supported), LOADABLE and
                    // EXT (Load PS, basic and extended form), GF (the CGCSGID fields are present).
                    // MULTID 0: every set has the one slot size (MS 0), so no two sets need one
                    // LCID. Byte 5 X'40': PSCS, a Load PS may load symbols smaller than the slot
                    // (p. 6-23). SDW, SDH, FORM types 1 to 6 (X'7E000000'; type 8, vector, is not
                    // built), descriptor length 7.
                    AppendBytes(stackalloc byte[] { 0xB2, 0x40 });
                    AppendByte((byte)CellWidth);
                    AppendByte((byte)CellHeight);
                    AppendUInt32(ProgrammedSymbolStore.FormTypes);
                    AppendByte(0x07);

                    // Base set: SET 0, flags 0, local ID X'F0', then character set and code page.
                    // p. 6-26: "Nonloadable character sets will have default LCID assigned to them
                    // (X'F0' to X'FE'), where: X'F0' to X'F7' = 1-byte character sets". The base
                    // set is non-loadable and 1-byte. Which ID in that range is not given by the
                    // manual; X'F1' is the Graphic Escape set's, X'F0' is the lowest free one
                    // (decided 24 November 2026). X'00' is not an ID: in SA and SFE it means
                    // "Default character set" (p. 4-19), a value a host sends. SA and SFE values
                    // are stored as sent and not interpreted here (GetCharacterCharacterSet,
                    // GetFieldCharacterSet), so X'00' and X'F0' both reach the caller, which
                    // must treat both as the base set and X'F1' as code page 310.
                    AppendBytes(stackalloc byte[] { 0x00, 0x00, BaseCharacterSetId });
                    AppendUInt16(_codePage.CharacterSet);
                    AppendUInt16(_codePage.Number);

                    // Graphic Escape set: SET 1, flags 0, local ID X'F1', code page 310.
                    AppendBytes(stackalloc byte[] { 0x01, 0x00, GraphicEscapeCharacterSetId });
                    AppendUInt16(CodePage.Cp310.CharacterSet);
                    AppendUInt16(CodePage.Cp310.Number);

                    // The loadable sets: SET = the RWS number (p. 6-25), flags LOAD, TRIPLE on a
                    // colour screen, CB as the last Load PS set it; the LCID now tied to the store
                    // (X'FF' when free, p. 6-25); CGCSGID all zeros, "a user-defined programmed
                    // symbol set" has none (p. 6-28).
                    for (int i = 0; i < ProgrammedSymbols.Count; i++)
                    {
                        ProgrammedSymbolSet set = ProgrammedSymbols[i];
                        AppendByte(set.Rws);
                        AppendByte((byte)(0x80 | (Color ? 0x40 : 0x00) | (set.NoLcidCompare ? 0x10 : 0x00)));
                        AppendByte(set.Lcid);
                        AppendUInt32(0);
                    }

                    break;

                case QueryCode.Color:
                {
                    // p. 6-29, 6-30. Flags 0, 8 pairs: the default X'00' is green (X'F4'), then
                    // X'F1'-X'F7' shown as themselves on a colour screen, as the default on a
                    // monochrome one.
                    AppendBytes(stackalloc byte[] { 0x00, 0x08, 0x00, 0xF4 });
                    for (byte c = 0xF1; c <= 0xF7; c++)
                    {
                        AppendByte(c);
                        AppendByte(Color ? c : (byte)0x00);
                    }

                    break;
                }

                case QueryCode.Highlighting:
                    // p. 6-56. 5 pairs: default X'00' is normal, normal, blink, reverse, underscore.
                    AppendBytes(stackalloc byte[] { 0x05, 0x00, 0xF0, 0xF0, 0xF0, 0xF1, 0xF1, 0xF2, 0xF2, 0xF4, 0xF4 });
                    break;

                case QueryCode.FieldValidation:
                    // p. 6-52: mandatory fill, mandatory entry and trigger.
                    AppendByte((byte)(FieldValidation.MandatoryFill | FieldValidation.MandatoryEntry | FieldValidation.Trigger));
                    break;

                case QueryCode.ReplyModes:
                    // p. 6-74: Field, Extended Field, Character.
                    AppendBytes(stackalloc byte[] { 0x00, 0x01, 0x02 });
                    break;

                case QueryCode.Ddm:
                    // p. 6-37. Flags 0, LIMIN, LIMOUT, one subset, subset ID 1.
                    AppendBytes(stackalloc byte[] { 0x00, 0x00 });
                    AppendUInt16(_fileTransferLimit);
                    AppendUInt16(_fileTransferLimit);
                    AppendBytes(stackalloc byte[] { 0x01, 0x01 });
                    break;

                case QueryCode.ImplicitPartition:
                    // p. 6-59 to 6-61. Flags 0, then the size self-defining parameter X'01', length
                    // 11: flags 0, default width and height, alternate width and height.
                    AppendBytes(stackalloc byte[] { 0x00, 0x00, 0x0B, 0x01, 0x00 });
                    AppendUInt16(_defaultColumns);
                    AppendUInt16(_defaultRows);
                    AppendUInt16(_alternateColumns);
                    AppendUInt16(_alternateRows);
                    break;
            }

            // Fill in the length, which counts itself.
            int length = _inbound.WrittenCount - start;
            Span<byte> written = MemoryMarshalWritten(start);
            written[0] = (byte)(length >> 8);
            written[1] = (byte)length;
        }

        private Span<byte> MemoryMarshalWritten(int start)
        {
            // ArrayBufferWriter only hands out the written bytes read-only; the length bytes are ours.
            return System.Runtime.InteropServices.MemoryMarshal.AsMemory(_inbound.WrittenMemory).Span.Slice(start);
        }

        private void AppendByte(byte value)
        {
            Span<byte> s = _inbound.GetSpan(1);
            s[0] = value;
            _inbound.Advance(1);
        }

        private void AppendBytes(ReadOnlySpan<byte> values)
        {
            values.CopyTo(_inbound.GetSpan(values.Length));
            _inbound.Advance(values.Length);
        }

        private void AppendUInt32(uint value)
        {
            AppendUInt16((int)(value >> 16));
            AppendUInt16((int)(value & 0xFFFF));
        }

        private void AppendUInt16(int value)
        {
            Span<byte> s = _inbound.GetSpan(2);
            s[0] = (byte)(value >> 8);
            s[1] = (byte)value;
            _inbound.Advance(2);
        }

        private static string Hex(byte value) => value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture);

        private int ProgramTab(int address, bool nullFill, out bool nullingWrapped)
        {
            nullingWrapped = false;
            int size = Size;

            // On the attribute of an unprotected field: move on by one only (p. 4-7).
            if (_p.IsFieldAttributeAt[address] && !FieldAttribute.IsProtected(_p.Buffer[address]))
            {
                return Next(address);
            }

            int a = address;
            if (nullFill)
            {
                // Nulls from here to the end of the field, whatever the protection (p. 4-7). The
                // search ends at the last location of the buffer.
                while (!_p.IsFieldAttributeAt[a])
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                    if (a == size - 1)
                    {
                        nullingWrapped = true;
                        return 0;
                    }

                    a++;
                }
            }

            // Next unprotected field attribute, stopping at the last location (p. 4-7).
            for (int i = a; i < size; i++)
            {
                if (_p.IsFieldAttributeAt[i] && !FieldAttribute.IsProtected(_p.Buffer[i]))
                {
                    // One found in the last location also gives address 0.
                    return i == size - 1 ? 0 : i + 1;
                }
            }

            return 0;
        }

        private bool ReadAddress(ReadOnlySpan<byte> data, ref int i, out int address)
        {
            address = 0;
            if (i + 1 >= data.Length)
            {
                Error("Buffer address cut short by the end of the record (p. 4-1).");
                return false;
            }

            if (_p.SixteenBitAddressing)
            {
                // A 16-bit partition: "all 8 bits of each byte are used for the address" (p. 4-4,
                // D-1), so every pair of bytes is in the proper form and only the range is checked.
                address = (data[i] << 8) | data[i + 1];
            }
            else if (!BufferAddress.TryDecode(data[i], data[i + 1], out address))
            {
                Error("Buffer address with the reserved flag bits B'10' is rejected (p. 4-4).");
                return false;
            }

            i += 2;
            if (address >= Size)
            {
                // "the write stops at once, with a negative response" (p. 4-3).
                Error("Buffer address " + address + " is outside the " + Size + "-cell screen (p. 4-3).");
                return false;
            }

            return true;
        }

        private bool ReadPairs(ReadOnlySpan<byte> data, ref int i, out int start, out int count)
        {
            start = 0;
            count = 0;
            if (i >= data.Length)
            {
                Error("SFE or MF without its pair count (p. 4-2, 4-6).");
                return false;
            }

            count = data[i++];
            start = i;
            if (i + (count * 2) > data.Length)
            {
                Error("SFE or MF pairs cut short by the end of the record (p. 4-1).");
                return false;
            }

            i += count * 2;
            return true;
        }

        private bool ApplyFieldPairs(int address, ReadOnlySpan<byte> pairs, bool fromSfe)
        {
            if (fromSfe)
            {
                // SFE sets every type not given to its default (p. 4-2).
                ClearFieldAttributes(address);
            }

            for (int p = 0; p + 1 < pairs.Length; p += 2)
            {
                byte type = pairs[p];
                byte value = pairs[p + 1];
                switch (type)
                {
                    case 0xC0:
                        // Keep the MDT the field already has? No: the pair carries the whole attribute.
                        _p.Buffer[address] = (byte)(value & FieldAttribute.Meaningful);
                        break;
                    case 0xC1:
                        if ((value & FieldValidation.Reserved) != 0)
                        {
                            Error("Field validation X'" + Hex(value) + "' sets the reserved bits 0-4 (p. 4-21).");
                            return false;
                        }

                        _p.FieldValidationAt[address] = value;
                        break;
                    case 0xC2:
                        _p.FieldOutliningAt[address] = value;
                        break;
                    case 0x41:
                        _p.FieldHighlight[address] = value;
                        break;
                    case 0x42:
                        _p.FieldColor[address] = value;
                        break;
                    case 0x43:
                        _p.FieldCharset[address] = value;
                        break;
                    case 0x45:
                        _p.FieldBackground[address] = value;
                        break;
                    case 0x46:
                        _p.FieldTransparency[address] = value;
                        break;
                    default:
                        Error("Attribute type X'" + type.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' is not valid in SFE or MF (p. 4-16).");
                        return false;
                }
            }

            return true;
        }

        private bool ApplySetAttribute(byte type, byte value)
        {
            switch (type)
            {
                case 0x00:
                    // All character attributes back to default (p. 4-17).
                    _saHighlight = _saColor = _saCharset = _saBackground = _saTransparency = 0;
                    return true;
                case 0x41:
                    _saHighlight = value;
                    return true;
                case 0x42:
                    _saColor = value;
                    return true;
                case 0x43:
                    _saCharset = value;
                    return true;
                case 0x45:
                    _saBackground = value;
                    return true;
                case 0x46:
                    _saTransparency = value;
                    return true;
                default:
                    Error("Attribute type X'" + type.ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + "' is not valid in SA (p. 4-16).");
                    return false;
            }
        }

        private void EraseAllUnprotected()
        {
            _p.PrimedField = -1;

            // GA23-0059-4 p. 3-7.
            bool any = false;
            bool isProtected = true;
            int size = Size;
            int first = GetFieldAttributeFor(size - 1);
            if (first >= 0)
            {
                isProtected = FieldAttribute.IsProtected(_p.Buffer[first]);
            }

            for (int a = 0; a < size; a++)
            {
                if (_p.IsFieldAttributeAt[a])
                {
                    isProtected = FieldAttribute.IsProtected(_p.Buffer[a]);
                    if (!isProtected)
                    {
                        any = true;
                        _p.Buffer[a] = (byte)(_p.Buffer[a] & ~FieldAttribute.Mdt);
                    }
                }
                else if (!isProtected || first < 0)
                {
                    StoreCharacter(a, FormatControl.Null, graphicEscape: false, useSa: false);
                    any = true;
                }
            }

            // EAU restores the keyboard (p. 3-7) and acknowledges (p. 3-23); it is a write to
            // the partition, so a primed trigger field is unprimed (p. 4-23).
            _deviceLock = KeyboardLock.None;
            _p.SysLock = false;
            _p.PrimedTrigger = -1;
            _lastAid = Aid.None;
            AcknowledgeInbound();
            CursorAddress = any ? NextInputPosition(size - 1) : 0;
        }

        private void SendReadModified(byte aid, bool all)
        {
            // Read Modified is the same in Field and Extended field mode: SBA and the data, no
            // attributes. Character mode adds SA orders for the listed types (p. 5-61, 5-62).
            _inbound.Clear();
            Array.Clear(_inboundSa);
            if (aid == Aid.SysReq)
            {
                // Test request read: SOH % / STX instead of the AID and cursor (p. 9-3; see SysReq).
                AppendByte(0x01);
                AppendByte(0x6C);
                AppendByte(0x61);
                AppendByte(0x02);
            }
            else
            {
                BeginInbound(aid);
            }

            if (!all && Aid.IsShortRead(aid))
            {
                // Short read: the AID alone (p. 3-13).
                Send();
                return;
            }

            if (aid != Aid.SysReq)
            {
                AppendAddress(CursorAddress);
            }

            // Selector-pen attention: each modified field's address, but none of its data (p. 3-12).
            bool sendData = all || aid != Aid.SelectorPen;
            int size = Size;

            if (!IsFormatted)
            {
                // Unformatted: all data from 0 to the end, nulls left out, no SBA (p. 3-13).
                for (int a = 0; a < size; a++)
                {
                    AppendData(a);
                }

                Send();
                return;
            }

            // Every modified field in buffer order: SBA (attribute + 1), then its data without nulls
            // (p. 3-13). A field that runs past the end of the buffer continues at address 0.
            for (int fa = 0; fa < size; fa++)
            {
                if (!_p.IsFieldAttributeAt[fa] || (_p.Buffer[fa] & FieldAttribute.Mdt) == 0)
                {
                    continue;
                }

                AppendByte(Order3270.SetBufferAddress);
                int a = Next(fa);
                AppendAddress(a);
                while (sendData && !_p.IsFieldAttributeAt[a])
                {
                    AppendData(a);
                    a = Next(a);
                }
            }

            Send();
        }

        private void SendReadBuffer(byte aid)
        {
            // AID, cursor, then every position from 0; nulls are NOT left out (p. 3-11, 4-10). How a
            // field attribute goes depends on the reply mode (p. 5-61, 5-62):
            //  - Field: SF and the attribute.
            //  - Extended field and Character: SFE with the attribute and every extended field
            //    attribute that is not at its default.
            //  - Character also sends SA before a character whose listed character attributes
            //    differ from the ones last sent in this record, which start at default.
            _inbound.Clear();
            Array.Clear(_inboundSa);
            BeginInbound(aid);
            AppendAddress(CursorAddress);
            int size = Size;
            for (int a = 0; a < size; a++)
            {
                if (_p.IsFieldAttributeAt[a])
                {
                    AppendFieldAttribute(a);
                    continue;
                }

                AppendCharacterAttributes(a);
                if (_p.GraphicEscapeAt[a])
                {
                    AppendByte(Order3270.GraphicEscape);
                }

                AppendByte(_p.Buffer[a]);
            }

            Send();
        }

        private void AppendFieldAttribute(int address)
        {
            // Bits 0-1 made printable per Figure D-1 (p. 4-13).
            byte attribute = BufferAddress.EncodeSixBits(_p.Buffer[address]);
            if (_p.InboundReplyMode == ReplyMode.Field)
            {
                AppendByte(Order3270.StartField);
                AppendByte(attribute);
                return;
            }

            Span<byte> pairs = stackalloc byte[16];
            int n = 0;
            pairs[n++] = 0xC0;
            pairs[n++] = attribute;
            AddPair(pairs, ref n, 0x41, _p.FieldHighlight[address]);
            AddPair(pairs, ref n, 0x42, _p.FieldColor[address]);
            AddPair(pairs, ref n, 0x43, _p.FieldCharset[address]);
            AddPair(pairs, ref n, 0x45, _p.FieldBackground[address]);
            AddPair(pairs, ref n, 0x46, _p.FieldTransparency[address]);
            AddPair(pairs, ref n, 0xC1, _p.FieldValidationAt[address]);
            AddPair(pairs, ref n, 0xC2, _p.FieldOutliningAt[address]);
            AppendByte(Order3270.StartFieldExtended);
            AppendByte((byte)(n / 2));
            AppendBytes(pairs.Slice(0, n));
        }

        private static void AddPair(Span<byte> pairs, ref int n, byte type, byte value)
        {
            // Default values are not sent (p. 5-61).
            if (value != 0)
            {
                pairs[n++] = type;
                pairs[n++] = value;
            }
        }

        private void AppendCharacterAttributes(int address)
        {
            // Character mode only, and only the types Set Reply Mode listed (p. 5-62).
            if (_p.InboundReplyMode != ReplyMode.Character)
            {
                return;
            }

            for (int i = 0; i < _p.ReplyTypes.Length; i++)
            {
                byte type = _p.ReplyTypes[i];
                int slot = Array.IndexOf(CharacterAttributeTypes, type);
                byte value = CharacterAttribute(slot, address);
                if (value != _inboundSa[slot])
                {
                    _inboundSa[slot] = value;
                    AppendByte(Order3270.SetAttribute);
                    AppendByte(type);
                    AppendByte(value);
                }
            }
        }

        private byte CharacterAttribute(int slot, int address)
        {
            // Slots in the order of CharacterAttributeTypes.
            switch (slot)
            {
                case 0:
                    return _p.CharHighlight[address];
                case 1:
                    return _p.CharColor[address];
                case 2:
                    return _p.CharCharset[address];
                case 3:
                    return _p.CharBackground[address];
                default:
                    return _p.CharTransparency[address];
            }
        }

        private void AppendData(int address)
        {
            // Nulls are left out; in Character mode SA orders go before a non-null character only
            // (p. 3-13, 5-62).
            byte b = _p.Buffer[address];
            if (b == FormatControl.Null)
            {
                return;
            }

            AppendCharacterAttributes(address);
            if (_p.GraphicEscapeAt[address])
            {
                AppendByte(Order3270.GraphicEscape);
            }

            AppendByte(b);
        }

        // The start of a read reply. From a partition with a nonzero PID it is an Inbound 3270DS:
        // AID X'88', then L, X'80', PID, AID, and the cursor and data follow (p. 6-7, 3-9 note 1);
        // Send fills in L. From partition 0, implicit or explicit, it is the plain 3270 reply that
        // starts with the AID (p. 6-7: "Orders and data from partition zero ... are transmitted in
        // 3270 format").
        private void BeginInbound(byte aid)
        {
            if (_explicitState && _p.Pid != 0)
            {
                AppendByte(Aid.StructuredField);
                _inboundFieldStart = _inbound.WrittenCount;
                AppendBytes(stackalloc byte[] { 0x00, 0x00, StructuredFieldId.Inbound3270DS, _p.Pid });
            }

            AppendByte(aid);
        }

        private void AppendAddress(int address)
        {
            Span<byte> s = _inbound.GetSpan(2);
            if (_p.SixteenBitAddressing)
            {
                // "When 16-bit addressing is specified, the partition will always generate 16-bit
                // addresses in the inbound data stream" (p. 4-4): all 16 bits, high byte first
                // (p. D-1: 3100 is X'0C1C').
                s[0] = (byte)(address >> 8);
                s[1] = (byte)address;
            }
            else
            {
                BufferAddress.Encode(address, Size, s);
            }

            _inbound.Advance(2);
        }

        private void Send()
        {
            // The record kept for a retry is the inbound operation's (an enter, a trigger, a Read
            // Partition), kept by KeepLastInboundForRetry; a plain read command's reply in a
            // retry state does not replace it (p. 3-17: the retry is of the inbound operation).
            if (_inboundFieldStart >= 0)
            {
                // The Inbound 3270DS length counts itself and runs to the end of the record.
                int fieldLength = _inbound.WrittenCount - _inboundFieldStart;
                Span<byte> written = MemoryMarshalWritten(_inboundFieldStart);
                written[0] = (byte)(fieldLength >> 8);
                written[1] = (byte)fieldLength;
                _inboundFieldStart = -1;
            }

            InboundRecord?.Invoke(_inbound.WrittenSpan);
        }

        private int NextInputPosition(int from)
        {
            // First position of the next unprotected field after "from", wrapping once; none: 0.
            int size = Size;
            int a = from;
            for (int n = 0; n < size; n++)
            {
                a = Next(a);
                if (_p.IsFieldAttributeAt[a] && !FieldAttribute.IsProtected(_p.Buffer[a]))
                {
                    int first = Next(a);
                    if (!_p.IsFieldAttributeAt[first])
                    {
                        return first;
                    }
                }
            }

            return 0;
        }

        private void SetFieldAttribute(int address, byte attribute)
        {
            _p.IsFieldAttributeAt[address] = true;
            _p.GraphicEscapeAt[address] = false;
            _p.Buffer[address] = attribute;

            // SF sets the extended field attribute of the position to its default (p. 4-2).
            ClearFieldAttributes(address);
            ClearCharacterAttributes(address);
        }

        private void StoreCharacter(int address, byte value, bool graphicEscape, bool useSa)
        {
            // The host may overwrite a field attribute with data (p. 4-13).
            _p.IsFieldAttributeAt[address] = false;
            ClearFieldAttributes(address);
            _p.Buffer[address] = value;
            _p.GraphicEscapeAt[address] = graphicEscape;
            if (useSa)
            {
                _p.CharHighlight[address] = _saHighlight;
                _p.CharColor[address] = _saColor;
                _p.CharCharset[address] = _saCharset;
                _p.CharBackground[address] = _saBackground;
                _p.CharTransparency[address] = _saTransparency;
            }
            else
            {
                // Overwriting or erasing a character replaces its character attribute (p. 4-15).
                ClearCharacterAttributes(address);
            }
        }

        private void ClearFieldAttributes(int address)
        {
            _p.FieldHighlight[address] = 0;
            _p.FieldColor[address] = 0;
            _p.FieldCharset[address] = 0;
            _p.FieldBackground[address] = 0;
            _p.FieldTransparency[address] = 0;
            _p.FieldValidationAt[address] = 0;
            _p.FieldOutliningAt[address] = 0;
        }

        private void ClearCharacterAttributes(int address)
        {
            _p.CharHighlight[address] = 0;
            _p.CharColor[address] = 0;
            _p.CharCharset[address] = 0;
            _p.CharBackground[address] = 0;
            _p.CharTransparency[address] = 0;
        }

        // Resizes and erases the partition being worked on (see Partition3270.Resize).
        private void Resize(int rows, int columns) => _p.Resize(rows, columns);

        private Partition3270? PartitionFor(byte pid)
        {
            if (!_explicitState)
            {
                return pid == 0 ? _implicitPartition : null;
            }

            return pid < _partitions.Length ? _partitions[pid] : null;
        }

        // Back to implicit partition state (p. 2-6, 2-7, 3-6 step 6b, 5-14, 5-15): every explicit
        // partition destroyed, implicit partition 0 made at the given size with its default
        // characteristics, INPID 0 and INOP Read Modified, so no inbound record is kept for a retry
        // (p. 3-19, 3-20). resetReplyMode false only for the UNBIND, see Unbind.
        private void ResetToImplicit(int rows, int columns, bool resetReplyMode = true)
        {
            // System Lock survives the reset (this display's reading: p. 2-9 ends it only by a
            // keyboard restore or RESET); PWAIT and the read state do not - every partition and
            // with it INPID's operation is gone (p. 3-19, 3-20).
            bool systemLock = ActivePartition.SysLock;
            Array.Clear(_partitions);
            _explicitState = false;
            _usableRows = 0;
            _usableColumns = 0;
            _p = _implicitPartition;
            _p.Resize(rows, columns);
            _p.Cursor = 0;
            _p.PrimedField = -1;
            if (resetReplyMode)
            {
                _p.InboundReplyMode = ReplyMode.Field;
                _p.ReplyTypes = Array.Empty<byte>();
            }

            _p.Pwait = false;
            _p.SysLock = systemLock;
            _p.PrimedTrigger = -1;
            _activePid = 0;
            _inboundPid = 0;
            _retryLength = 0;
            _readState = ReadState.Normal;
            _inop = Command3270.ReadModified;
            _triggerPending = false;
            _queueCount = 0;
        }

        // After a host record: the code works on the active partition again (see _p).
        private void SelectActive() => _p = ActivePartition;

        private int Next(int address)
        {
            int n = address + 1;
            return n == Size ? 0 : n;
        }

        private void Error(string message)
        {
            _failed = true;
            ProtocolError?.Invoke(message);
        }
    }

    /// <summary>
    /// Receives one record the terminal sends to the host.
    /// </summary>
    /// <param name="record">
    /// The record, valid only during the call.
    /// </param>
    public delegate void InboundRecordHandler(ReadOnlySpan<byte> record);

    /// <summary>
    /// Receives the text of a protocol error.
    /// </summary>
    /// <param name="message">
    /// What was rejected or not built, and the page of the manual that decides it.
    /// </param>
    public delegate void ProtocolErrorHandler(string message);

    /// <summary>
    /// Called when the host asks the terminal to sound its alarm.
    /// </summary>
    public delegate void AlarmHandler();

    /// <summary>
    /// Receives one structured field from a Write Structured Field.
    /// </summary>
    /// <param name="field">
    /// The whole structured field, its two length bytes first, valid only during the call.
    /// </param>
    public delegate void StructuredFieldHandler(ReadOnlySpan<byte> field);
}
