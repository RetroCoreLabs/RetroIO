# Plan: outstanding work only

Next: CP/M (item 2): write the CP/M reader with the C128 1571 layout, read-only, tested on TurboPascal.d71.

Proof level for all items: lean. One full suite run after a merge, a mutation check for the key new rule only.

## RetroCore (the sibling RetroCore repository)

RetroCore master (local, not pushed; the user pushes RetroCore) has the Dragon 64 and CoCo 3 boot work, the MOOH cartridge, the Becker port and `Nuget/BUGS.md`. The Dragon items (quick load for `.cas`, Dragon 32 graphics bands with Juxtaposition) are in `Machines.Dragon/TODO.md`; the Becker port is on all five 6809 machines (CoCo 1/2/3, Dragon 32/64); the CoCo 1/2 has a cartridge slot and boots HDB-DOS from the port (`Coco2BeckerDiskBasicTests`). DriveWire is done; the bit-banger serial line is not being built (owner's decision, 2026-10-31).

## RetroIO

1. **NuGet publish**: needs the `NUGET_API_KEY` secret and tag `v0.1.0`; the user said "not yet".
2. **CP/M disks** (approved 2026-11-21, C128 first). Read, then write, CP/M disks from the
   Commodore 128, from CP/M-86 on the PC, and from Z80/8080 machines.

   - **Why it is one job, not three.** Every CP/M disk uses the same filesystem: a directory of
     32-byte entries (user number, 8.3 name, extent number, record count, and the list of allocation
     blocks the file uses). What differs between machines is only the disk *layout*: sector size,
     sectors per track, sector skew, how many tracks are reserved for the system, block size and
     directory size. CP/M itself describes that layout with a small table, the Disk Parameter Block
     (DPB) plus a skew table, defined in Digital Research's "CP/M 2.2 Alteration Guide" and
     "CP/M 3 System Guide". So: **one CP/M filesystem, plus a list of machine layouts**, each layout
     being one DPB written as data (the same "geometry is data" rule `RetroIO.Storage.Local` already
     follows).
   - **CP/M 2.2 and CP/M 3 (CP/M Plus).** CP/M 3 adds date and time stamps, passwords and a disk
     label in the directory (the C128 runs CP/M 3). Read both; write CP/M 2.2 first, CP/M 3 stamps
     after.
   - **Machines, in this order.**
     1. **Commodore 128 CP/M** on 1541, 1571 and 1581 images. The disk has a normal CBM header
        named "CP/M" and CP/M data elsewhere; we already have one real image (a C128 CP/M D71).
        Layout from Commodore's C128 CP/M documentation.
     2. **CP/M-86 on PC floppies**: the 160/320 KB IBM layouts, the 360 KB and 1.2 MB later ones.
        Uses the existing PC floppy images.
     3. **Z80/8080 machines**: Kaypro II/4, Osborne 1, Amstrad CPC/PCW (data and system formats),
        Epson QX-10, and the 8-inch IBM 3740 standard layout CP/M was born on. Each layout from the
        maker's technical manual.
   - **Image containers.** Raw images first. Then the containers these machines' images come in:
     the CPC/PCW `.dsk` (plain and extended), and ImageDisk `.imd`. Teledisk `.td0` only if needed.
   - **Detection.** CP/M has no signature. The C128 disk is found by its CBM header. Others are found
     by trying each layout that fits the image size and keeping the one whose directory makes sense
     (valid user numbers and names, extents in order, blocks inside the disk). When two layouts both
     fit, say so and let the caller choose, never guess silently.
   - **Checking it.** Tests against real disk images of each machine, read back by an independent
     CP/M disk tool used only as a check in the tests (as VICE c1541 and Toolshed are used for
     Commodore and OS-9), never as the design source. The design cites only the Digital Research
     manuals and the makers' documentation.
   - **Mount in RetroCommander** (the sibling RetroCommander repository). Done by the RetroCommander session,
     not here: when the RetroIO CP/M reading and writing is finished and its tests pass, send that
     session a message (SendMessage) asking it to add CP/M disk support, with what follows. A CP/M disk opens like any
     other image. When detection finds one layout, it is used. When several fit, or none, the user
     picks the machine from a list ("Kaypro II", "Osborne 1 double density", "C128 1571", ...) and
     the choice is remembered for that image. An "advanced" panel lets a user enter an unknown
     layout by hand (sector size, sectors per track, reserved tracks, block size, directory entries,
     skew) and save it as a new named layout.
   - **CP/M machines in RetroCore** (the sibling RetroCore repository), so these disks can be mounted
     and booted: today it has one simple 8080 CP/M machine and the C128; an x86 machine exists for
     CP/M-86. New Z80 machines (Kaypro, Osborne, Amstrad) are separate work there, planned in
     RetroCore when the RetroIO side reads their disks.
   - **Found so far**: the C128 CP/M 3 BIOS disk module source (CXDISK, 15 Apr 86) lists the
     C64 CP/M 2.2, C128 single-sided, C128 double-sided (1571) and 1581 layouts, with no skew on the
     Commodore formats, and recognises a C128 CP/M disk by "CBM" in the first bytes of track 1
     sector 0. The `dpb` macro (CPM3.LIB) and the constants (CXEQU) are read. The routine that turns a CP/M
     256-byte record into a Commodore track and sector is in the C128 Z80 ROM (4 KB): RST 5 jumps
     through a table at 0100h; entry 50h = 03E3h (track and sector), 52h = 046Bh (the "CBM" check,
     returns the sector's last byte, FF = double-sided). 03E3h, disassembled: record 680 and up is
     side 2 (subtract 680, track + 35); add 2 (track 1 positions 0 and 1 are never CP/M); zones of
     355+2 records at 21 sectors (tracks 1-17), then +1 (track 18 position 0 = the CBM header is
     skipped, the rest of track 18 IS CP/M), 133 at 19 (18-24), 108 at 18 (25-30), the rest at 17;
     the position in the track goes through a skew table at 0FB5h (21, 19, 18, 17 entries, steps of
     5). Checked by replaying it on TurboPascal.d71: the directory and TURBO.MSG read back right.
     Two directory entries there have blank names; look at them when writing the reader. Measured on real
     images: 1541/1571 C128 CP/M disks have "CBM" at track 1 sector 0 and its last byte FF on a
     double-sided disk; the directory starts on track 1 sector 10 and goes 10, 15, 20, 4 (steps of
     5 around the track); 1581 disks have no "CBM" mark and the directory at track 1 sector 0.
     Images downloaded (corpus `cpm/c128`, `cpm/kaypro`): Turbo Pascal (D71, D81, lessons D81),
     MBASIC, WordStar 4, CP/M 3 system disks, C64 CP/M 2.2, utilities; five Kaypro disks.
     cpmtools 2.20 is installed in WSL as the check tool; its built-in format list has no Commodore
     entries.
   - **Still needed**: Amstrad images (approved, not yet downloaded).
