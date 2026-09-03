namespace RetroIO.Protocols.Xmsg.Api
{
    /// <summary>
    /// Why a sender is or is not allowed to update this system's routing tables.
    /// </summary>
    /// <remarks>
    /// <para><b>Two independent things have to be true</b></para>
    /// A sender is authorised only when BOTH the sending task and its system are privileged, and
    /// the two are earned separately: a task becomes privileged by calling XFPRV with the password,
    /// a remote system by being defined as a friend of ours through the XROUT service XSDAT. This
    /// enumeration is the answer to "which of the two is missing".
    /// <para>
    /// From the COSMOS Programmer Guide section 4.7 (line 10631), where these are the values the D
    /// register carries back.
    /// </para>
    /// <para><b>Cross-checked against version L, 2026-08-07 - and it was worth checking</b></para>
    /// The Programmer Guide is based on XMSG version <b>J</b>, and ND's version-L program
    /// description (210373L) section 6.4 opens with "The response from the XFCPV function has been
    /// changed" - so the guide could easily have been describing superseded behaviour. It is not.
    /// The L document lists the same four reasons in the same order:
    ///  - D=0 unprivileged remote system AND unprivileged XMSG task.
    ///  - D=1 privileged system, but not privileged XMSG task.
    ///  - D=2 XMSG privileged task, but not privileged remote system.
    ///  - D=3 the specified message is a returned (non-delivery) message.
    /// <para>
    /// It also confirms the authorised reading that <see cref="NeitherPrivileged"/> doubles up on:
    /// when A=1, D=0 means the message came from a task in the LOCAL system and D=1 from another
    /// system. That is why the two must be read together, and it is why a local sender reports
    /// <see cref="SystemOnly"/> rather than <see cref="NeitherPrivileged"/> - a local system is
    /// privileged by definition, which corrected a test earlier in this project.
    /// </para>
    /// </remarks>
    public enum XmsgPrivilegeInformation
    {
        /// <summary>
        /// Neither the sending task nor its system is privileged.
        /// </summary>
        /// <remarks>
        /// Also the value returned alongside an AUTHORISED result, where it means something else
        /// entirely - that the message came from a task inside the local system. Read it together
        /// with <see cref="XmsgPrivilegeCheck.IsAuthorised"/>, never alone.
        /// </remarks>
        NeitherPrivileged = 0,

        /// <summary>
        /// The source system is privileged but the sending task is not.
        /// </summary>
        /// <remarks>
        /// Also the value returned alongside an AUTHORISED result, where it means the message came
        /// from another system rather than this one.
        /// </remarks>
        SystemOnly = 1,

        /// <summary>
        /// The sending task is privileged but the source system is not.
        /// </summary>
        TaskOnly = 2,

        /// <summary>
        /// The message is a returned, undelivered message, so there is no sender to judge.
        /// </summary>
        ReturnedMessage = 3,
    }

    /// <summary>
    /// The result of checking a message sender's privileges (XFCPV).
    /// </summary>
    /// <remarks>
    /// <para><b>What the two fields are</b></para>
    /// The A register carries the verdict and the D register carries the detail - and the detail
    /// means different things depending on the verdict, which is why they are read together here
    /// rather than exposed as two loose numbers.
    /// <para>
    /// COSMOS Programmer Guide section 4.7 (line 10631). Authorisation means specifically that the
    /// sender may execute the privileged XROUT services XSDRN and XSDSY against this system - it is
    /// permission to change our routing tables, not a general trust level.
    /// </para>
    /// </remarks>
    public readonly struct XmsgPrivilegeCheck
    {
        /// <summary>
        /// Initialises the result.
        /// </summary>
        /// <param name="status">
        /// The completion status.
        /// </param>
        /// <param name="isAuthorised">
        /// Whether the sender may update this system's routing tables.
        /// </param>
        /// <param name="information">
        /// The detail behind the verdict.
        /// </param>
        public XmsgPrivilegeCheck(XmsgStatus status, bool isAuthorised, XmsgPrivilegeInformation information)
        {
            Status = status;
            IsAuthorised = isAuthorised;
            Information = information;
        }

        /// <summary>
        /// Gets the completion status of the check itself.
        /// </summary>
        /// <remarks>
        /// An error here means the message could not be examined - it says nothing about the
        /// sender's privileges.
        /// </remarks>
        public XmsgStatus Status { get; }

        /// <summary>
        /// Gets whether the sender may update this system's routing tables.
        /// </summary>
        /// <remarks>
        /// The A register: 1 when allowed, 0 when not.
        /// </remarks>
        public bool IsAuthorised { get; }

        /// <summary>
        /// Gets the detail behind the verdict.
        /// </summary>
        /// <remarks>
        /// Its meaning depends on <see cref="IsAuthorised"/>. When authorised it says WHERE the
        /// message came from - <see cref="XmsgPrivilegeInformation.NeitherPrivileged"/> for the
        /// local system and <see cref="XmsgPrivilegeInformation.SystemOnly"/> for another. When not
        /// authorised it says WHICH privilege was missing. The two readings share the same two
        /// values, so this must never be interpreted on its own.
        /// </remarks>
        public XmsgPrivilegeInformation Information { get; }

        /// <summary>
        /// Gets whether the message examined was a returned, undelivered one.
        /// </summary>
        /// <remarks>
        /// A convenience over the one detail value that is unambiguous: it can only appear on a
        /// refusal, because a returned message has no sender to authorise.
        /// </remarks>
        public bool IsReturnedMessage
        {
            get { return !IsAuthorised && Information == XmsgPrivilegeInformation.ReturnedMessage; }
        }
    }
}
