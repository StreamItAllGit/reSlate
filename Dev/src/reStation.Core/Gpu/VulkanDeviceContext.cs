// @module: reStation.Core.Gpu.VulkanDeviceContext
// @purpose: Clean-room hardware Vulkan 1.x logical device, queue, command pool, staging buffer, and VK_FORMAT_R16_UINT 1024x512 VkImage VRAM manager.
// @depends-on: System, System.Text, reStation.Core.Gpu.VulkanBindings
// @exports: VulkanDeviceContext
// @owner: devv-gpu-backends (T3)

using System.Text;

namespace reStation.Core.Gpu;

internal sealed unsafe class VulkanDeviceContext : IDisposable
{
    private const ulong StagingBytes = PsxGpu.VramWidth * PsxGpu.VramHeight * sizeof(ushort);
    private const ulong FenceTimeoutNs = 5_000_000_000UL;

    private readonly VulkanBindings _vk;
    private IntPtr _cmdBuffer;
    private ulong _cmdPool, _fence, _stagingBuf, _stagingMem;
    private void* _stagingMapped;
    private uint _vramLayout = VulkanConst.VkImageLayoutUndefined;
    private bool _disposed;

    private VulkanDeviceContext(VulkanBindings vk) => _vk = vk;

    public IntPtr Instance { get; private set; }
    public IntPtr PhysicalDevice { get; private set; }
    public IntPtr Device { get; private set; }
    public IntPtr Queue { get; private set; }
    public ulong VramImage { get; private set; }
    public ulong VramImageMemory { get; private set; }
    public ulong VramImageView { get; private set; }
    public uint QueueFamilyIndex { get; private set; }
    public string DeviceName { get; private set; } = string.Empty;
    public string ApiVersionText { get; private set; } = string.Empty;
    public bool IsValid => !_disposed && Device != IntPtr.Zero && VramImage != 0;

    public static bool TryCreate(out VulkanDeviceContext? context, out string diagnostic, IReadOnlyList<string>? libraryCandidates = null)
    {
        context = null;
        if (!VulkanBindings.TryLoad(out VulkanBindings? vk, out diagnostic, libraryCandidates) || vk is null)
            return false;

        var ctx = new VulkanDeviceContext(vk);
        if (!ctx.InitializeInternal(out diagnostic))
        {
            ctx.Dispose();
            return false;
        }
        context = ctx;
        return true;
    }

    private bool InitializeInternal(out string diagnostic)
    {
        if (!CreateInstance(out diagnostic) ||
            !SelectPhysicalDeviceAndCreateDevice(out diagnostic) ||
            !CreateCommandAndSyncObjects(out diagnostic) ||
            !CreateR16UiVramImage(out diagnostic) ||
            !CreateStagingBuffer(out diagnostic))
            return false;

        if (!TryUploadVramRect(0, 0, PsxGpu.VramWidth, PsxGpu.VramHeight, new ushort[PsxGpu.VramWidth * PsxGpu.VramHeight]))
        {
            diagnostic = "Initial Vulkan R16_UINT 1024x512 VRAM image transition/upload failed.";
            return false;
        }
        diagnostic = $"Vulkan {ApiVersionText} active on '{DeviceName}' (R16_UINT {PsxGpu.VramWidth}x{PsxGpu.VramHeight} VkImage=0x{VramImage:X}).";
        return true;
    }

    private bool CreateInstance(out string diagnostic)
    {
        byte[] appName = Encoding.UTF8.GetBytes("reSlate\0");
        byte[] engName = Encoding.UTF8.GetBytes("reEngine\0");
        fixed (byte* pApp = appName, pEng = engName)
        {
            var appInfo = new VkAppInfo
            {
                SType = 0u, PAppName = pApp, AppVersion = 1, PEngineName = pEng, EngineVersion = 1,
                ApiVersion = VulkanConst.MakeApiVersion(1, 1, 0)
            };
            var instInfo = new VkInstCreateInfo { SType = 1u, PAppInfo = &appInfo };
            IntPtr inst = IntPtr.Zero;
            int res = _vk.CreateInstance(&instInfo, IntPtr.Zero, &inst);
            if (res == VulkanConst.VkErrorIncompatibleDriver)
            {
                appInfo.ApiVersion = VulkanConst.MakeApiVersion(1, 0, 0);
                res = _vk.CreateInstance(&instInfo, IntPtr.Zero, &inst);
            }
            if (res != VulkanConst.VkSuccess || inst == IntPtr.Zero)
            {
                diagnostic = $"vkCreateInstance failed (VkResult={res}).";
                return false;
            }
            Instance = inst;
        }
        if (!_vk.LoadInstanceProcs(Instance)) { diagnostic = "Failed resolving Vulkan instance procedures."; return false; }
        diagnostic = string.Empty;
        return true;
    }

    private bool SelectPhysicalDeviceAndCreateDevice(out string diagnostic)
    {
        uint count = 0;
        _ = _vk.EnumeratePhysicalDevices(Instance, &count, null);
        if (count == 0) { diagnostic = "No Vulkan physical devices available."; return false; }
        uint cap = Math.Min(count, 16u);
        IntPtr* devs = stackalloc IntPtr[(int)cap];
        _ = _vk.EnumeratePhysicalDevices(Instance, &cap, devs);

        IntPtr bestDev = IntPtr.Zero;
        uint bestQFamily = 0;
        int bestScore = -1;
        string bestName = string.Empty;
        uint bestApi = 0;
        var qProps = stackalloc VkQFamilyProps[16];

        for (uint i = 0; i < cap; i++)
        {
            VkPhysDevPropsBuffer props;
            _vk.GetPhysicalDeviceProperties(devs[i], &props);
            uint qCount = 0;
            _vk.GetPhysicalDeviceQueueFamilyProperties(devs[i], &qCount, null);
            uint qCap = Math.Min(qCount, 16u);
            _vk.GetPhysicalDeviceQueueFamilyProperties(devs[i], &qCap, qProps);

            uint foundQ = uint.MaxValue;
            for (uint q = 0; q < qCap; q++)
            {
                if ((qProps[q].QueueFlags & VulkanConst.VkQueueGraphicsBit) != 0) { foundQ = q; break; }
                if (foundQ == uint.MaxValue && (qProps[q].QueueFlags & VulkanConst.VkQueueTransferBit) != 0) foundQ = q;
            }
            if (foundQ == uint.MaxValue) continue;

            if (_vk.GetPhysicalDeviceFormatProperties != null)
            {
                VkFmtProps fmtProps;
                _vk.GetPhysicalDeviceFormatProperties(devs[i], VulkanConst.VkFormatR16Uint, &fmtProps);
                if (fmtProps.OptimalTilingFeatures != 0 && (fmtProps.OptimalTilingFeatures & VulkanConst.VkFormatFeatureSampledImageBit) == 0)
                    continue;
            }

            int score = props.DeviceType == 2u ? 100 : props.DeviceType == 1u ? 50 : 10;
            if ((qProps[foundQ].QueueFlags & VulkanConst.VkQueueGraphicsBit) != 0) score += 10;
            if (score > bestScore)
            {
                bestScore = score; bestDev = devs[i]; bestQFamily = foundQ;
                bestName = props.ReadDeviceName(); bestApi = props.ApiVersion;
            }
        }

        if (bestDev == IntPtr.Zero) { diagnostic = "No Vulkan physical device supports graphics/transfer + R16_UINT."; return false; }
        PhysicalDevice = bestDev;
        QueueFamilyIndex = bestQFamily;
        DeviceName = string.IsNullOrWhiteSpace(bestName) ? "Vulkan GPU" : bestName;
        ApiVersionText = $"{(bestApi >> 22) & 0x7FFu}.{(bestApi >> 12) & 0x3FFu}.{bestApi & 0xFFFu}";

        float prio = 1f;
        var qInfo = new VkDevQueueCreateInfo { SType = 2u, QueueFamilyIndex = QueueFamilyIndex, QueueCount = 1, PQueuePriorities = &prio };
        var devInfo = new VkDevCreateInfo { SType = 3u, QueueCreateInfoCount = 1, PQueueCreateInfos = &qInfo };
        IntPtr dev = IntPtr.Zero;
        int res = _vk.CreateDevice(PhysicalDevice, &devInfo, IntPtr.Zero, &dev);
        if (res != VulkanConst.VkSuccess || dev == IntPtr.Zero) { diagnostic = $"vkCreateDevice failed (VkResult={res})."; return false; }
        Device = dev;
        if (!_vk.LoadDeviceProcs(Device)) { diagnostic = "Failed resolving Vulkan device procedures."; return false; }
        IntPtr qHandle = IntPtr.Zero;
        _vk.GetDeviceQueue(Device, QueueFamilyIndex, 0, &qHandle);
        Queue = qHandle;
        diagnostic = string.Empty;
        return Queue != IntPtr.Zero;
    }

    private bool CreateCommandAndSyncObjects(out string diagnostic)
    {
        var poolInfo = new VkCmdPoolCreateInfo { SType = 39u, Flags = VulkanConst.VkCommandPoolCreateResetBit, QueueFamilyIndex = QueueFamilyIndex };
        ulong pool = 0, fence = 0;
        if (_vk.CreateCommandPool(Device, &poolInfo, IntPtr.Zero, &pool) != VulkanConst.VkSuccess || pool == 0)
        { diagnostic = "vkCreateCommandPool failed."; return false; }
        _cmdPool = pool;

        var allocInfo = new VkCmdBufAllocInfo { SType = 40u, CommandPool = _cmdPool, Level = 0, CommandBufferCount = 1 };
        IntPtr cmd = IntPtr.Zero;
        if (_vk.AllocateCommandBuffers(Device, &allocInfo, &cmd) != VulkanConst.VkSuccess || cmd == IntPtr.Zero)
        { diagnostic = "vkAllocateCommandBuffers failed."; return false; }
        _cmdBuffer = cmd;

        var fenceInfo = new VkFenceCreateInfo { SType = 8u, Flags = VulkanConst.VkFenceCreateSignaledBit };
        if (_vk.CreateFence(Device, &fenceInfo, IntPtr.Zero, &fence) != VulkanConst.VkSuccess || fence == 0)
        { diagnostic = "vkCreateFence failed."; return false; }
        _fence = fence;
        diagnostic = string.Empty;
        return true;
    }

    private bool CreateR16UiVramImage(out string diagnostic)
    {
        var imgInfo = new VkImgCreateInfo
        {
            SType = 14u, ImageType = VulkanConst.VkImageType2D, Format = VulkanConst.VkFormatR16Uint,
            ExtentW = PsxGpu.VramWidth, ExtentH = PsxGpu.VramHeight, ExtentD = 1,
            MipLevels = 1, ArrayLayers = 1, Samples = 1, Tiling = 0,
            Usage = VulkanConst.VkImageUsageTransferSrcBit | VulkanConst.VkImageUsageTransferDstBit | VulkanConst.VkImageUsageSampledBit,
            InitialLayout = VulkanConst.VkImageLayoutUndefined
        };
        ulong img = 0, mem = 0, view = 0;
        if (_vk.CreateImage(Device, &imgInfo, IntPtr.Zero, &img) != VulkanConst.VkSuccess || img == 0)
        { diagnostic = "vkCreateImage(R16_UINT 1024x512) failed."; return false; }
        VramImage = img;

        VkMemReqs req;
        _vk.GetImageMemoryRequirements(Device, img, &req);
        uint memIdx = FindMemoryType(req.MemoryTypeBits, VulkanConst.VkMemoryPropertyDeviceLocalBit);
        if (memIdx == uint.MaxValue) memIdx = FindMemoryType(req.MemoryTypeBits, 0);
        if (memIdx == uint.MaxValue) { diagnostic = "No compatible Vulkan memory type for R16_UINT VRAM image."; return false; }

        var alloc = new VkMemAllocInfo { SType = 5u, AllocationSize = req.Size, MemoryTypeIndex = memIdx };
        if (_vk.AllocateMemory(Device, &alloc, IntPtr.Zero, &mem) != VulkanConst.VkSuccess ||
            _vk.BindImageMemory(Device, img, mem, 0) != VulkanConst.VkSuccess)
        {
            if (mem != 0) _vk.FreeMemory(Device, mem, IntPtr.Zero);
            diagnostic = "Failed allocating/binding R16_UINT VRAM VkImage memory.";
            return false;
        }
        VramImageMemory = mem;

        var viewInfo = new VkImgViewCreateInfo
        {
            SType = 15u, Image = img, ViewType = VulkanConst.VkImageViewType2D, Format = VulkanConst.VkFormatR16Uint,
            SubresourceRange = new VkImgSubresRange { AspectMask = 1, LevelCount = 1, LayerCount = 1 }
        };
        if (_vk.CreateImageView(Device, &viewInfo, IntPtr.Zero, &view) != VulkanConst.VkSuccess || view == 0)
        { diagnostic = "vkCreateImageView(R16_UINT) failed."; return false; }
        VramImageView = view;
        diagnostic = string.Empty;
        return true;
    }

    private bool CreateStagingBuffer(out string diagnostic)
    {
        var bufInfo = new VkBufCreateInfo
        {
            SType = 12u, Size = StagingBytes, Usage = VulkanConst.VkBufferUsageTransferSrcBit | VulkanConst.VkBufferUsageTransferDstBit
        };
        ulong buf = 0, mem = 0;
        if (_vk.CreateBuffer(Device, &bufInfo, IntPtr.Zero, &buf) != VulkanConst.VkSuccess || buf == 0)
        { diagnostic = "vkCreateBuffer(staging) failed."; return false; }
        _stagingBuf = buf;

        VkMemReqs req;
        _vk.GetBufferMemoryRequirements(Device, buf, &req);
        uint memIdx = FindMemoryType(req.MemoryTypeBits, VulkanConst.VkMemoryPropertyHostVisibleBit | VulkanConst.VkMemoryPropertyHostCoherentBit);
        if (memIdx == uint.MaxValue) { diagnostic = "No HOST_VISIBLE|HOST_COHERENT Vulkan memory type for staging buffer."; return false; }

        var alloc = new VkMemAllocInfo { SType = 5u, AllocationSize = req.Size, MemoryTypeIndex = memIdx };
        void* mapped = null;
        if (_vk.AllocateMemory(Device, &alloc, IntPtr.Zero, &mem) != VulkanConst.VkSuccess ||
            _vk.BindBufferMemory(Device, buf, mem, 0) != VulkanConst.VkSuccess ||
            _vk.MapMemory(Device, mem, 0, StagingBytes, 0, &mapped) != VulkanConst.VkSuccess || mapped == null)
        {
            if (mem != 0) _vk.FreeMemory(Device, mem, IntPtr.Zero);
            diagnostic = "Failed allocating/mapping Vulkan staging buffer.";
            return false;
        }
        _stagingMem = mem;
        _stagingMapped = mapped;
        diagnostic = string.Empty;
        return true;
    }

    public bool TryUploadVramRect(int x, int y, int w, int h, ReadOnlySpan<ushort> fullMirror)
    {
        if (!IsValid || w <= 0 || h <= 0 || fullMirror.Length < PsxGpu.VramWidth * PsxGpu.VramHeight) return false;
        int nx = x & 1023, ny = y & 511, cw = Math.Min(w, PsxGpu.VramWidth), ch = Math.Min(h, PsxGpu.VramHeight);
        var regions = stackalloc VkBufImgCopy[4];
        int regCount = 0;
        ulong byteOff = 0;
        ushort* dst = (ushort*)_stagingMapped;
        int w1 = Math.Min(cw, PsxGpu.VramWidth - nx), w2 = cw - w1;
        int h1 = Math.Min(ch, PsxGpu.VramHeight - ny), h2 = ch - h1;

        fixed (ushort* pSrc = fullMirror)
        {
            StageSubRect(pSrc, dst, regions, ref regCount, ref byteOff, nx, ny, w1, h1);
            if (w2 > 0) StageSubRect(pSrc, dst, regions, ref regCount, ref byteOff, 0, ny, w2, h1);
            if (h2 > 0) StageSubRect(pSrc, dst, regions, ref regCount, ref byteOff, nx, 0, w1, h2);
            if (w2 > 0 && h2 > 0) StageSubRect(pSrc, dst, regions, ref regCount, ref byteOff, 0, 0, w2, h2);
        }
        if (regCount == 0) return true;
        return ExecuteSingleTimeTransfer(toDst: true, regions, (uint)regCount);
    }

    private static void StageSubRect(
        ushort* pSrc, ushort* dst, VkBufImgCopy* regions, ref int regCount, ref ulong byteOff,
        int rx, int ry, int rw, int rh)
    {
        if (rw <= 0 || rh <= 0) return;
        regions[regCount++] = new VkBufImgCopy
        {
            BufferOffset = byteOff,
            ImageSubresource = new VkImgSubresLayers { AspectMask = 1, LayerCount = 1 },
            OffsetX = rx, OffsetY = ry, ExtentWidth = (uint)rw, ExtentHeight = (uint)rh, ExtentDepth = 1
        };
        for (int r = 0; r < rh; r++)
        {
            int srcRow = ((ry + r) & 511) * PsxGpu.VramWidth;
            Buffer.MemoryCopy(pSrc + srcRow + rx, dst + (byteOff / 2UL) + ((ulong)r * (ulong)rw), (long)rw * 2L, (long)rw * 2L);
        }
        byteOff += (ulong)rw * (ulong)rh * 2UL;
    }

    public bool TryReadVramRect(int x, int y, int w, int h, Span<ushort> destination)
    {
        if (!IsValid || w <= 0 || h <= 0 || destination.Length < w * h) return false;
        int nx = x & 1023, ny = y & 511, cw = Math.Min(w, PsxGpu.VramWidth), ch = Math.Min(h, PsxGpu.VramHeight);
        var regions = stackalloc VkBufImgCopy[4];
        var subMeta = stackalloc int[4 * 4]; // dstCol, dstRow, rw, rh
        int regCount = 0;
        ulong byteOff = 0;

        void QueueSub(int rx, int ry, int rw, int rh, int dstCol, int dstRow)
        {
            if (rw <= 0 || rh <= 0) return;
            subMeta[(regCount * 4) + 0] = dstCol; subMeta[(regCount * 4) + 1] = dstRow;
            subMeta[(regCount * 4) + 2] = rw; subMeta[(regCount * 4) + 3] = rh;
            regions[regCount++] = new VkBufImgCopy
            {
                BufferOffset = byteOff,
                ImageSubresource = new VkImgSubresLayers { AspectMask = 1, LayerCount = 1 },
                OffsetX = rx, OffsetY = ry, ExtentWidth = (uint)rw, ExtentHeight = (uint)rh, ExtentDepth = 1
            };
            byteOff += (ulong)rw * (ulong)rh * 2UL;
        }

        int w1 = Math.Min(cw, PsxGpu.VramWidth - nx), w2 = cw - w1;
        int h1 = Math.Min(ch, PsxGpu.VramHeight - ny), h2 = ch - h1;
        QueueSub(nx, ny, w1, h1, 0, 0);
        if (w2 > 0) QueueSub(0, ny, w2, h1, w1, 0);
        if (h2 > 0) QueueSub(nx, 0, w1, h2, 0, h1);
        if (w2 > 0 && h2 > 0) QueueSub(0, 0, w2, h2, w1, h1);
        if (regCount == 0 || !ExecuteSingleTimeTransfer(toDst: false, regions, (uint)regCount)) return false;

        ushort* src = (ushort*)_stagingMapped;
        for (int idx = 0; idx < regCount; idx++)
        {
            int dstCol = subMeta[(idx * 4) + 0], dstRow = subMeta[(idx * 4) + 1];
            int rw = subMeta[(idx * 4) + 2], rh = subMeta[(idx * 4) + 3];
            ushort* regSrc = src + (regions[idx].BufferOffset / 2UL);
            for (int r = 0; r < rh; r++)
            {
                int outBase = ((dstRow + r) * w) + dstCol;
                for (int c = 0; c < rw; c++) destination[outBase + c] = regSrc[(r * rw) + c];
            }
        }
        return true;
    }

    private bool ExecuteSingleTimeTransfer(bool toDst, VkBufImgCopy* regions, uint regionCount)
    {
        ulong fence = _fence;
        if (_vk.WaitForFences(Device, 1, &fence, 1, FenceTimeoutNs) != VulkanConst.VkSuccess) return false;
        if (_vk.ResetFences(Device, 1, &fence) != VulkanConst.VkSuccess) return false;
        _ = _vk.ResetCommandBuffer(_cmdBuffer, 0);
        var beginInfo = new VkCmdBufBeginInfo { SType = 42u, Flags = VulkanConst.VkCommandBufferOneTimeSubmitBit };
        if (_vk.BeginCommandBuffer(_cmdBuffer, &beginInfo) != VulkanConst.VkSuccess) return false;

        var range = new VkImgSubresRange { AspectMask = 1, LevelCount = 1, LayerCount = 1 };
        uint transferLayout = toDst ? VulkanConst.VkImageLayoutTransferDstOptimal : VulkanConst.VkImageLayoutTransferSrcOptimal;
        uint dstAccess = toDst ? VulkanConst.VkAccessTransferWriteBit : VulkanConst.VkAccessTransferReadBit;
        var toTransfer = new VkImgMemBarrier
        {
            SType = 45u, SrcAccessMask = _vramLayout == VulkanConst.VkImageLayoutUndefined ? 0u : VulkanConst.VkAccessTransferWriteBit,
            DstAccessMask = dstAccess, OldLayout = _vramLayout, NewLayout = transferLayout,
            SrcQueueFamilyIndex = VulkanConst.VkQueueFamilyIgnored, DstQueueFamilyIndex = VulkanConst.VkQueueFamilyIgnored,
            Image = VramImage, SubresourceRange = range
        };
        _vk.CmdPipelineBarrier(_cmdBuffer, VulkanConst.VkPipelineStageTopOfPipeBit | VulkanConst.VkPipelineStageTransferBit, VulkanConst.VkPipelineStageTransferBit, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, &toTransfer);

        if (toDst) _vk.CmdCopyBufferToImage(_cmdBuffer, _stagingBuf, VramImage, transferLayout, regionCount, regions);
        else _vk.CmdCopyImageToBuffer(_cmdBuffer, VramImage, transferLayout, _stagingBuf, regionCount, regions);

        var toGeneral = new VkImgMemBarrier
        {
            SType = 45u, SrcAccessMask = dstAccess, DstAccessMask = VulkanConst.VkAccessMemoryReadBit,
            OldLayout = transferLayout, NewLayout = VulkanConst.VkImageLayoutGeneral,
            SrcQueueFamilyIndex = VulkanConst.VkQueueFamilyIgnored, DstQueueFamilyIndex = VulkanConst.VkQueueFamilyIgnored,
            Image = VramImage, SubresourceRange = range
        };
        _vk.CmdPipelineBarrier(_cmdBuffer, VulkanConst.VkPipelineStageTransferBit, VulkanConst.VkPipelineStageBottomOfPipeBit, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, &toGeneral);
        if (_vk.EndCommandBuffer(_cmdBuffer) != VulkanConst.VkSuccess) return false;

        IntPtr cmd = _cmdBuffer;
        var submit = new VkSubmitInfo { SType = 4u, CommandBufferCount = 1, PCommandBuffers = &cmd };
        if (_vk.QueueSubmit(Queue, 1, &submit, _fence) != VulkanConst.VkSuccess) return false;
        if (_vk.WaitForFences(Device, 1, &fence, 1, FenceTimeoutNs) != VulkanConst.VkSuccess) return false;
        _vramLayout = VulkanConst.VkImageLayoutGeneral;
        return true;
    }

    private uint FindMemoryType(uint typeBits, uint requiredFlags)
    {
        VkPhysMemProps props;
        _vk.GetPhysicalDeviceMemoryProperties(PhysicalDevice, &props);
        for (uint i = 0; i < props.MemoryTypeCount; i++)
        {
            if ((typeBits & (1u << (int)i)) != 0 && (props.MemoryTypes[(int)(i * 2)] & requiredFlags) == requiredFlags)
                return i;
        }
        return uint.MaxValue;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Device != IntPtr.Zero)
        {
            _ = _vk.DeviceWaitIdle(Device);
            if (_stagingMapped != null) { _vk.UnmapMemory(Device, _stagingMem); _stagingMapped = null; }
            if (_stagingBuf != 0) { _vk.DestroyBuffer(Device, _stagingBuf, IntPtr.Zero); _stagingBuf = 0; }
            if (_stagingMem != 0) { _vk.FreeMemory(Device, _stagingMem, IntPtr.Zero); _stagingMem = 0; }
            if (VramImageView != 0) { _vk.DestroyImageView(Device, VramImageView, IntPtr.Zero); VramImageView = 0; }
            if (VramImage != 0) { _vk.DestroyImage(Device, VramImage, IntPtr.Zero); VramImage = 0; }
            if (VramImageMemory != 0) { _vk.FreeMemory(Device, VramImageMemory, IntPtr.Zero); VramImageMemory = 0; }
            if (_fence != 0) { _vk.DestroyFence(Device, _fence, IntPtr.Zero); _fence = 0; }
            if (_cmdPool != 0) { _vk.DestroyCommandPool(Device, _cmdPool, IntPtr.Zero); _cmdPool = 0; }
            _vk.DestroyDevice(Device, IntPtr.Zero);
            Device = IntPtr.Zero;
        }
        if (Instance != IntPtr.Zero) { _vk.DestroyInstance(Instance, IntPtr.Zero); Instance = IntPtr.Zero; }
        _vk.Dispose();
    }
}
