namespace Aurora386.Core.Memory;

public sealed unsafe partial class PhysicalMemoryBus
{
    // Only pages covered by a relocatable window need an underlying entry.
    // Keeping logical mappings here avoids capturing transient read-watch
    // pointers or duplicating the full four-gigabyte page table.
    private readonly Dictionary<int, PageMapping> _coveredBasePages = new();
    private readonly List<DeviceMapping> _deviceMappings = new();

    /// <summary>
    /// Creates a disabled, relocatable device window. Later-created windows
    /// take precedence where they overlap; moving a window retains its order.
    /// This is the bus's deterministic mapping policy, not PCI arbitration.
    /// Owners update or disable their window when hardware decoding changes.
    /// </summary>
    public DeviceMapping CreateDeviceMapping(IMemoryDevice device)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(device);

        var mapping = new DeviceMapping(this, device);
        _deviceMappings.Add(mapping);
        return mapping;
    }

    /// <summary>
    /// One device's address decoder, such as a 1990s PCI display aperture.
    /// The device receives offsets relative to the currently programmed base.
    /// Disabling or disposing the window reveals the current underlying map.
    /// Use on the emulation thread, like the bus's other mapping operations.
    /// </summary>
    public sealed class DeviceMapping : IDisposable
    {
        private readonly PhysicalMemoryBus _bus;
        private readonly IMemoryDevice _device;
        private bool _disposed;

        internal DeviceMapping(PhysicalMemoryBus bus, IMemoryDevice device)
        {
            _bus = bus;
            _device = device;
        }

        public uint PhysicalBase { get; private set; }
        public uint Length { get; private set; }
        public bool Enabled { get; private set; }

        /// <summary>
        /// Enables decoding at a new page-aligned range. Invalid ranges leave
        /// the previous mapping intact, including its precedence over peers.
        /// </summary>
        public void Update(uint physicalBase, uint length)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(_bus._disposed, _bus);
            _bus.ValidateRange(physicalBase, length);

            if (Enabled && physicalBase == PhysicalBase && length == Length)
            {
                return;
            }

            uint previousBase = PhysicalBase;
            uint previousLength = Enabled ? Length : 0;
            PhysicalBase = physicalBase;
            Length = length;
            Enabled = true;

            // Publish the complete new decoder before rebuilding either range.
            // Otherwise overlap between the old and new ranges could briefly
            // recapture a device as its own underlying mapping.
            _bus.RefreshMappings(previousBase, previousLength);
            _bus.RefreshMappings(physicalBase, length);
        }

        /// <summary>Stops decoding without discarding the programmed range.</summary>
        public void Disable()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ObjectDisposedException.ThrowIf(_bus._disposed, _bus);
            if (!Enabled)
            {
                return;
            }

            Enabled = false;
            _bus.RefreshMappings(PhysicalBase, Length);
        }

        /// <summary>Releases this decoder; repeated disposal is harmless.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (!_bus._disposed)
            {
                Disable();
                _bus._deviceMappings.Remove(this);
            }

            Enabled = false;
            _disposed = true;
        }

        internal bool Covers(uint physicalAddress)
        {
            return Enabled && physicalAddress >= PhysicalBase
                && (ulong)physicalAddress < (ulong)PhysicalBase + Length;
        }

        internal IMemoryDevice Device => _device;
    }

    /// <summary>
    /// The logical base mapping beneath a relocatable decoder. RAM always
    /// refers to the installed memory at this physical page, including ROM.
    /// </summary>
    private readonly record struct PageMapping(
        bool Ram, IMemoryDevice? Device, uint DeviceBase, bool ReadOnly);

    /// <summary>
    /// Changes the underlying motherboard map even when a PCI window hides it.
    /// Permanent mappings keep their ordinary replace-in-place semantics.
    /// </summary>
    private void SetBaseMapping(int page, PageMapping mapping)
    {
        if (_coveredBasePages.ContainsKey(page))
        {
            _coveredBasePages[page] = mapping;
            return;
        }

        ApplyPageMapping(page, mapping);
    }

    /// <summary>
    /// Recomputes only affected page caches after a decoder changes. Selecting
    /// precedence here leaves every CPU read and write on its existing path.
    /// </summary>
    private void RefreshMappings(uint physicalBase, uint length)
    {
        int firstPage = (int)(physicalBase >> PageShift);
        int pageCount = (int)(length >> PageShift);
        for (int page = firstPage; page < firstPage + pageCount; page++)
        {
            uint physicalAddress = (uint)page << PageShift;
            DeviceMapping? visible = null;
            for (int index = _deviceMappings.Count - 1; index >= 0; index--)
            {
                DeviceMapping candidate = _deviceMappings[index];
                if (candidate.Covers(physicalAddress))
                {
                    visible = candidate;
                    break;
                }
            }

            if (visible is null)
            {
                if (_coveredBasePages.Remove(page, out PageMapping underlying))
                {
                    ApplyPageMapping(page, underlying);
                }

                continue;
            }

            if (!_coveredBasePages.ContainsKey(page))
            {
                bool ram = _pageRam[page] is not null || _pageWatchedRam[page] is not null;
                _coveredBasePages.Add(page, new PageMapping(
                    ram, _pageDevice[page], _pageDeviceBase[page], _pageReadOnly[page]));
            }

            ApplyPageMapping(page,
                new PageMapping(false, visible.Device, visible.PhysicalBase, false));
        }
    }

    /// <summary>
    /// Installs a logical mapping with read-watch state derived from the current
    /// watch ranges. A stale watched RAM pointer must never bypass a device.
    /// </summary>
    private void ApplyPageMapping(int page, PageMapping mapping)
    {
        _pageRam[page] = null;
        _pageWatchedRam[page] = null;
        _pageDevice[page] = mapping.Device;
        _pageDeviceBase[page] = mapping.DeviceBase;
        _pageReadOnly[page] = mapping.ReadOnly;
        if (!mapping.Ram)
        {
            return;
        }

        uint physicalAddress = (uint)page << PageShift;
        foreach ((uint start, uint length) in _readWatchRanges)
        {
            bool overlaps = (ulong)physicalAddress < (ulong)start + length
                && (ulong)start < (ulong)physicalAddress + PageSize;
            if (overlaps)
            {
                _pageWatchedRam[page] = _ram + physicalAddress;
                return;
            }
        }

        _pageRam[page] = _ram + physicalAddress;
    }
}
