//
// SPDX-License-Identifier: MIT
// Copyright (c) 1985-2026 Ronny Hansen
// RetroCore Labs — https://github.com/RetroCoreLabs
// RetroIO - Accessing yesterday's data with today's standards
//

using System;
using System.Threading;
using RetroIO.Storage.Utilities;

namespace RetroIO.Tools.TnfsStress;

public class Program
{
    private const int BoxWidth = 74;

    public static void Main(string[] args)
    {
        ConsoleBox.Top(BoxWidth);
        ConsoleBox.Title("TNFS STRESS TEST TOOL", BoxWidth);
        ConsoleBox.Bottom(BoxWidth);
        Console.WriteLine();

        // Get username
        Console.Write("Username: ");
        string? username = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(username))
        {
            Console.WriteLine("Username cannot be empty!");
            return;
        }

        // Get password
        Console.Write("Password: ");
        string? password = ReadPassword();
        if (string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("\nPassword cannot be empty!");
            return;
        }
        Console.WriteLine();

        // Get thread count
        Console.Write("Number of threads: ");
        string? threadCountStr = Console.ReadLine();
        if (!int.TryParse(threadCountStr, out int threadCount) || threadCount <= 0 || threadCount > 100)
        {
            Console.WriteLine("Invalid thread count! Must be between 1 and 100.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Starting stress test with {threadCount} thread(s)...");
        Console.WriteLine("Press Ctrl+C to stop.");
        Console.WriteLine();

        // Create statistics tracker
        var stats = new Statistics();

        // Create worker threads
        var workers = new WorkerThread[threadCount];
        var threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            workers[i] = new WorkerThread(i + 1, "localhost", 16384, username, password, stats);
            threads[i] = new Thread(workers[i].Run);
            threads[i].IsBackground = false;
            threads[i].Name = $"Worker-{i + 1}";
        }

        // Start all threads
        for (int i = 0; i < threadCount; i++)
        {
            threads[i].Start();
        }

        // Handle Ctrl+C
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nStopping threads...");
            for (int i = 0; i < threadCount; i++)
            {
                workers[i].Stop();
            }
        };

        // Display statistics every second
        while (true)
        {
            Thread.Sleep(1000);

            // Clear console and display stats
            Console.Clear();
            stats.DisplayStats();

            // Check if all threads have stopped
            bool allStopped = true;
            for (int i = 0; i < threadCount; i++)
            {
                if (threads[i].IsAlive)
                {
                    allStopped = false;
                    break;
                }
            }

            if (allStopped)
            {
                Console.WriteLine();
                Console.WriteLine("All threads have stopped.");
                break;
            }
        }

        // Wait for all threads to complete
        for (int i = 0; i < threadCount; i++)
        {
            threads[i].Join();
        }

        Console.WriteLine("\nStress test complete. Press any key to exit.");
        Console.ReadKey();
    }

    /// <summary>
    /// Read password from console without echoing characters
    /// </summary>
    private static string ReadPassword()
    {
        var password = string.Empty;
        ConsoleKeyInfo key;

        do
        {
            key = Console.ReadKey(true);

            if (key.Key != ConsoleKey.Backspace && key.Key != ConsoleKey.Enter)
            {
                password += key.KeyChar;
                Console.Write("*");
            }
            else if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password = password.Substring(0, password.Length - 1);
                Console.Write("\b \b");
            }
        }
        while (key.Key != ConsoleKey.Enter);

        return password;
    }
}
