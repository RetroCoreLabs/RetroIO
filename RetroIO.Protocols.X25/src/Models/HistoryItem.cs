using System;

using RetroIO.Link.Models;
namespace RetroIO.Protocols.X25.Models
{
using X25 = RetroIO.Protocols.X25.Protocols.X25;

    /// <summary>
    /// Base class for history items in the ring buffer
    /// </summary>
    public abstract class HistoryItem
    {
        /// <summary>
        /// Unique history item ID
        /// </summary>
        public long Id { get; set; }

        /// <summary>
        /// Timestamp when item was created
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Layer type (HDLC/LAPB/X25)
        /// </summary>
        public LayerType Layer { get; set; }

        /// <summary>
        /// Direction (TX/RX)
        /// </summary>
        public Direction Direction { get; set; }

        /// <summary>
        /// Full payload data
        /// </summary>
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>
        /// Payload length
        /// </summary>
        public int Length => Payload.Length;

        /// <summary>
        /// Description of the item
        /// </summary>
        public string Description { get; set; } = "";

        /// <summary>
        /// Related history item (for parent-child relationships)
        /// </summary>
        public HistoryItem? RelatedItem { get; set; }

        /// <summary>
        /// Relationship type with related item
        /// </summary>
        public RelationshipType RelationshipType { get; set; } = RelationshipType.None;

        /// <summary>
        /// True if this is the main line in a two-line display (packet ID shown)
        /// </summary>
        public bool IsMainLine { get; set; } = true;

        /// <summary>
        /// True if this is a secondary line in a two-line display (shows "==>" prefix)
        /// </summary>
        public bool IsSecondaryLine { get; set; } = false;

        /// <summary>
        /// Create history item
        /// </summary>
        protected HistoryItem(LayerType layer, Direction direction, byte[] payload, string description)
        {
            Layer = layer;
            Direction = direction;
            Payload = payload ?? Array.Empty<byte>();
            Description = description ?? "";
            Timestamp = DateTime.UtcNow;
        }

        /// <summary>
        /// Get payload as hex string
        /// </summary>
        public string GetPayloadHex()
        {
            if (Payload.Length == 0)
                return "";
                
            return BitConverter.ToString(Payload).Replace("-", " ");
        }

        /// <summary>
        /// Get payload as string (if printable)
        /// </summary>
        public string GetPayloadString()
        {
            if (Payload.Length == 0)
                return "";
                
            try
            {
                return System.Text.Encoding.ASCII.GetString(Payload);
            }
            catch
            {
                return GetPayloadHex();
            }
        }

        /// <summary>
        /// Get summary string
        /// </summary>
        public virtual string GetSummary()
        {
            return $"{Layer}[{Direction}] Len={Length} {Description}";
        }

        /// <summary>
        /// Get detailed string representation
        /// </summary>
        public override string ToString()
        {
            return $"[{Id}] {GetSummary()}";
        }
    }

    /// <summary>
    /// Layer types
    /// </summary>
    public enum LayerType
    {
        /// <summary>
        /// HDLC layer
        /// </summary>
        HDLC = 0,

        /// <summary>
        /// LAPB layer
        /// </summary>
        LAPB = 1,

        /// <summary>
        /// X.25 layer
        /// </summary>
        X25 = 2
    }

    /// <summary>
    /// Direction
    /// </summary>
    public enum Direction
    {
        /// <summary>
        /// Transmitted
        /// </summary>
        TX = 0,

        /// <summary>
        /// Received
        /// </summary>
        RX = 1
    }

    /// <summary>
    /// Relationship types between history items
    /// </summary>
    public enum RelationshipType
    {
        /// <summary>
        /// No relationship
        /// </summary>
        None = 0,

        /// <summary>
        /// This item encapsulates the related item (LAPB encapsulates X.25)
        /// </summary>
        Encapsulates = 1,

        /// <summary>
        /// This item is encapsulated by the related item (X.25 encapsulated by LAPB)
        /// </summary>
        EncapsulatedBy = 2
    }
}
