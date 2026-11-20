@echo off
rem Where the tests find the disk images and tools that are not in git.
rem
rem Copy this file to test-env.local.bat in the same folder (that name is in .gitignore, so it
rem never reaches GitHub), put the folders of YOUR machine in it, and run it in the console you
rem run the tests from:
rem
rem     src\eng\test-env.local.bat
rem     dotnet test src\RetroIO.slnx -nodeReuse:false
rem
rem It sets the variables for that console only; nothing is changed in Windows.
rem A variable left empty makes the tests that need it skip, with a message naming the variable.
rem The downloaded test set (src\eng\fetch-test-images) needs no variable.

rem Personal test-image collection: folders d81, dsk, vdk, img, ndfs, ...
set RETROIO_TEST_IMAGES=

rem Real ND and Sun hard-disk images: sun2-sunos-3.2.img, SUN2.img, 1325.img
set RETROIO_HDD_IMAGES=

rem 2.11BSD images taken from tape: the folder holding 1.FromTape and 1b.FromTape
set RETROIO_BSD_IMAGES=

rem Loose disk images: 211bsd.img, sun2-s2.0-disk.img
set RETROIO_LOOSE_IMAGES=

rem The NDIX source tree (holds rootfs_mkproto.img and the other NDIX filesystem images)
set RETROIO_NDIX_DIR=

rem The VICE c1541 program (full path to c1541.exe)
set C1541=

rem The x25emu folder as WSL sees it (a Linux path), for the CrossEnvLive tests
set X25EMU_DIR=

rem The folder the tnfsd server serves, for the live TNFS tests
set TNFS_TEST_ROOT=
