# Plan: outstanding work only

Next: CP/M plan (item 2) waits on the user's approval; NuGet publish waits on "not yet".

Proof level for all items: lean. One full suite run after a merge, a mutation check for the key new rule only.

## RetroCore (the sibling RetroCore repository)

RetroCore master (local, not pushed; the user pushes RetroCore) has the Dragon 64 and CoCo 3 boot work, the MOOH cartridge, the Becker port and `Nuget/BUGS.md`. The Dragon items (quick load for `.cas`, Dragon 32 graphics bands with Juxtaposition) are in `Machines.Dragon/TODO.md`; the Becker port is on all five 6809 machines (CoCo 1/2/3, Dragon 32/64); the CoCo 1/2 has a cartridge slot and boots HDB-DOS from the port (`Coco2BeckerDiskBasicTests`). DriveWire is done; the bit-banger serial line is not being built (owner's decision, 2026-10-31).

## RetroIO

1. **NuGet publish**: needs the `NUGET_API_KEY` secret and tag `v0.1.0`; the user said "not yet".
2. **CP/M disks** (plan, waiting for the user's approval). Read, then write, CP/M disks from the
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
   - **Still needed before code**: real disk images for each machine (we have one C128 image), and
     the independent check tool installed in WSL.
