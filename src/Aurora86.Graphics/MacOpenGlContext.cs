using System.Runtime.InteropServices;
using Aurora86.Core.Devices.Video;

namespace Aurora86.Graphics;

/// <summary>
/// Small native CGL binding for an accelerated OpenGL 3.2 core context.
/// Integer RGBA storage preserves Aurora's complete pixel word, including
/// alpha, without normalized colour conversion, blending or an SDL dependency.
/// Every method, including cleanup, runs on the backend's dedicated thread.
/// </summary>
internal sealed unsafe class MacOpenGlContext : IDisposable
{
    private const string FrameworkPath = "/System/Library/Frameworks/OpenGL.framework/OpenGL";
    private const int CglAccelerated = 73;
    private const int CglAllowOfflineRenderers = 96;
    private const int CglOpenGlProfile = 99;
    private const int CglCore32Profile = 0x3200;
    private const uint GlNoError = 0;
    private const uint GlVendor = 0x1F00;
    private const uint GlRenderer = 0x1F01;
    private const uint GlVersion = 0x1F02;
    private const uint GlMaxTextureSize = 0x0D33;
    private const uint GlTexture2D = 0x0DE1;
    private const uint GlTextureMinFilter = 0x2801;
    private const uint GlTextureMagFilter = 0x2800;
    private const int GlNearest = 0x2600;
    private const int GlRgba8UnsignedInteger = 0x8D7C;
    private const uint GlRgbaInteger = 0x8D99;
    private const uint GlUnsignedByte = 0x1401;
    private const uint GlFramebuffer = 0x8D40;
    private const uint GlColorAttachment0 = 0x8CE0;
    private const uint GlFramebufferComplete = 0x8CD5;
    private const uint GlColor = 0x1800;
    private const uint GlPackAlignment = 0x0D05;
    private const uint GlPackRowLength = 0x0D02;
    private const int MaximumErrorsToDrain = 16;
    private const uint GlVertexShader = 0x8B31;
    private const uint GlFragmentShader = 0x8B30;
    private const uint GlCompileStatus = 0x8B81;
    private const uint GlLinkStatus = 0x8B82;
    private const uint GlTexture0 = 0x84C0;
    private const uint GlTriangles = 0x0004;
    private const uint GlBlend = 0x0BE2;
    private const uint GlDither = 0x0BD0;
    private const uint GlUnpackAlignment = 0x0CF5;
    private const uint GlUnpackRowLength = 0x0CF2;

    private const string BatchFragmentSource = """
        #version 150
        uniform usampler2D destination;
        uniform uvec4 sourceColours[128];
        uniform uvec4 segmentMetadata[32];
        uniform int segmentCount;
        uniform int atlasWidth;
        out uvec4 resultColour;
        void main() {
            ivec2 point = ivec2(gl_FragCoord.xy);
            uint offset = uint(point.y * atlasWidth + point.x);
            uvec4 previous = texelFetch(destination, point, 0);
            resultColour = previous;
            for (int segment = 0; segment < 128; ++segment) {
                if (segment >= segmentCount) break;
                uint metadata = segmentMetadata[segment >> 2][segment & 3];
                if (offset < (metadata & 0x7fffffffu)) {
                    uvec4 source = sourceColours[segment];
                    if ((metadata & 0x80000000u) == 0u) {
                        resultColour = source;
                    } else {
                        uvec4 rounded = previous * (255u - source.a) + uvec4(128u);
                        resultColour = min(source + ((rounded + (rounded >> 8u)) >> 8u), uvec4(255u));
                    }
                    break;
                }
            }
        }
        """;
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UniformVectors(int location, int count, uint* values);
    private uint _batchProgram;
    private int _batchDestination, _batchColours, _batchMetadata, _batchCount, _batchWidth;
    private UniformVectors? _uniformVectors;
    public bool BatchSupported { get; private set; }
    public string BatchUnavailableReason { get; private set; } = "";

    private const string BlendVertexSource = """
        #version 150
        void main()
        {
            vec2 position = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
            gl_Position = vec4(position * 2.0 - 1.0, 0.0, 1.0);
        }
        """;
    private const string BlendFragmentSource = """
        #version 150
        uniform usampler2D destination;
        uniform uvec4 sourceColour;
        out uvec4 resultColour;
        void main()
        {
            uvec4 previous = texelFetch(destination, ivec2(gl_FragCoord.xy), 0);
            uvec4 rounded = previous * (255u - sourceColour.a) + uvec4(128u);
            uvec4 scaled = (rounded + (rounded >> 8u)) >> 8u;
            resultColour = min(sourceColour + scaled, uvec4(255u));
        }
        """;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ChoosePixelFormat(int* attributes, out nint format, out int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DescribePixelFormat(nint format, int screen, int attribute, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateContext(nint format, nint shared, out nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ContextOperation(nint context);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint GetString(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetError();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetInteger(uint name, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjectNames(int count, uint* names);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BindObject(uint target, uint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextureParameter(uint target, uint name, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextureImage(uint target, int level, int internalFormat,
        int width, int height, int border, uint format, uint type, nint pixels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FramebufferTexture(uint target, uint attachment,
        uint textureTarget, uint texture, int level);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint FramebufferStatus(uint target);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ClearBuffer(uint buffer, int drawBuffer, uint* values);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ReadPixels(int x, int y, int width, int height,
        uint format, uint type, void* pixels);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PixelStore(uint name, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SelectReadBuffer(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Finish();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CreateShader(uint type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ShaderSource(uint shader, int count, nint* strings, int* lengths);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void SingleObject(uint name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ObjectStatus(uint name, uint parameter, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint CreateProgram();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AttachShader(uint program, uint shader);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UniformLocation(uint program, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UniformSampler(int location, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UniformColour(int location, uint first, uint second, uint third, uint fourth);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void Viewport(int x, int y, int width, int height);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DrawArrays(uint mode, int first, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TextureSubImage(uint target, int level, int x, int y,
        int width, int height, uint format, uint type, void* pixels);

    private nint _library;
    private nint _context;
    private ContextOperation? _destroyContext;
    private ContextOperation? _setCurrentContext;
    private ObjectNames? _deleteTextures;
    private ObjectNames? _deleteFramebuffers;
    private uint _texture;
    private uint _framebuffer;
    private int _width;
    private int _height;
    private int _maximumTextureSize;
    private uint _blendProgram;
    private uint _blendVertexArray;
    private uint _inputTexture;
    private int _sourceColourLocation;
    private int _destinationLocation;
    private SingleObject? _deleteProgram;
    private SingleObject? _useProgram;
    private ObjectNames? _deleteVertexArrays;
    private SingleObject? _bindVertexArray;
    private SingleObject? _activeTexture;
    private SingleObject? _disable;
    private UniformSampler? _uniformSampler;
    private UniformColour? _uniformColour;
    private Viewport? _viewport;
    private DrawArrays? _drawArrays;
    private TextureSubImage? _textureSubImage;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly GetString _getString;
    private readonly GetError _getError;
    private readonly ObjectNames _generateTextures;
    private readonly ObjectNames _generateFramebuffers;
    private readonly BindObject _bindTexture;
    private readonly BindObject _bindFramebuffer;
    private readonly TextureParameter _textureParameter;
    private readonly TextureImage _textureImage;
    private readonly FramebufferTexture _framebufferTexture;
    private readonly FramebufferStatus _framebufferStatus;
    private readonly ClearBuffer _clearBuffer;
    private readonly ReadPixels _readPixels;
    private readonly PixelStore _pixelStore;
    private readonly SelectReadBuffer _readBuffer;
    private readonly Finish _finish;

    public bool BlendSupported { get; private set; }
    public string BlendUnavailableReason { get; private set; } = "";
    public string Renderer { get; }
    public string Vendor { get; }
    public string Version { get; }

    public MacOpenGlContext()
    {
        try
        {
            _library = NativeLibrary.Load(FrameworkPath);
            var choose = Load<ChoosePixelFormat>("CGLChoosePixelFormat");
            var describe = Load<DescribePixelFormat>("CGLDescribePixelFormat");
            var create = Load<CreateContext>("CGLCreateContext");
            var destroyFormat = Load<ContextOperation>("CGLDestroyPixelFormat");
            _destroyContext = Load<ContextOperation>("CGLDestroyContext");
            _setCurrentContext = Load<ContextOperation>("CGLSetCurrentContext");
            int* attributes = stackalloc int[] {
                CglOpenGlProfile, CglCore32Profile, CglAccelerated,
                CglAllowOfflineRenderers, 0
            };
            CheckCgl(choose(attributes, out nint format, out int count), "choosing an accelerated pixel format");
            if (format == 0 || count < 1)
            {
                if (format != 0)
                {
                    destroyFormat(format);
                }
                throw new InvalidOperationException("CGL found no accelerated pixel format.");
            }
            try
            {
                CheckCgl(describe(format, 0, CglAccelerated, out int accelerated), "checking acceleration");
                if (accelerated == 0)
                {
                    throw new InvalidOperationException("CGL selected an unaccelerated renderer.");
                }
                CheckCgl(create(format, 0, out _context), "creating the offscreen context");
            }
            finally
            {
                destroyFormat(format);
            }
            CheckCgl(_setCurrentContext(_context), "making the context current");
            _getString = Load<GetString>("glGetString");
            _getError = Load<GetError>("glGetError");
            _generateTextures = Load<ObjectNames>("glGenTextures");
            _deleteTextures = Load<ObjectNames>("glDeleteTextures");
            _generateFramebuffers = Load<ObjectNames>("glGenFramebuffers");
            _deleteFramebuffers = Load<ObjectNames>("glDeleteFramebuffers");
            _bindTexture = Load<BindObject>("glBindTexture");
            _bindFramebuffer = Load<BindObject>("glBindFramebuffer");
            _textureParameter = Load<TextureParameter>("glTexParameteri");
            _textureImage = Load<TextureImage>("glTexImage2D");
            _framebufferTexture = Load<FramebufferTexture>("glFramebufferTexture2D");
            _framebufferStatus = Load<FramebufferStatus>("glCheckFramebufferStatus");
            _clearBuffer = Load<ClearBuffer>("glClearBufferuiv");
            _readPixels = Load<ReadPixels>("glReadPixels");
            _pixelStore = Load<PixelStore>("glPixelStorei");
            _readBuffer = Load<SelectReadBuffer>("glReadBuffer");
            _finish = Load<Finish>("glFinish");
            Load<GetInteger>("glGetIntegerv")(GlMaxTextureSize, out _maximumTextureSize);
            Renderer = StringValue(GlRenderer);
            Vendor = StringValue(GlVendor);
            Version = StringValue(GlVersion);
            if (_maximumTextureSize <= 0 || Renderer.Length == 0 || _getError() != GlNoError)
            {
                throw new InvalidOperationException("The native OpenGL context is not usable.");
            }
            InitializeBlend();
            InitializeBatch();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private T Load<T>(string name) where T : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));
    }

    private string StringValue(uint name)
    {
        return Marshal.PtrToStringAnsi(_getString(name)) ?? "";
    }

    private static void CheckCgl(int result, string operation)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"CGL error {result} while {operation}.");
        }
    }

    public bool TryFill(int width, int height, uint colour, uint[] pixels)
    {
        CheckOwner();
        if (width <= 0 || height <= 0 || width > _maximumTextureSize ||
            height > _maximumTextureSize || (long)width * height > pixels.Length)
        {
            return false;
        }
        for (int error = 0; error < MaximumErrorsToDrain; error++)
        {
            if (_getError() == GlNoError)
            {
                break;
            }
            if (error == MaximumErrorsToDrain - 1)
            {
                return false;
            }
        }
        if (!EnsureStage(width, height))
        {
            return false;
        }
        _bindFramebuffer(GlFramebuffer, _framebuffer);
        uint* channels = stackalloc uint[] {
            colour & 0xFFu, (colour >> 8) & 0xFFu,
            (colour >> 16) & 0xFFu, colour >> 24
        };
        // Guest words are AARRGGBB; little-endian readback therefore needs B,G,R,A.
        _clearBuffer(GlColor, 0, channels);
        _readBuffer(GlColorAttachment0);
        _pixelStore(GlPackAlignment, 1);
        _pixelStore(GlPackRowLength, 0);
        fixed (uint* destination = pixels)
        {
            _readPixels(0, 0, width, height, GlRgbaInteger, GlUnsignedByte, destination);
        }
        _finish();
        return _getError() == GlNoError;
    }

    private uint CompileShader(uint type, string source)
    {
        uint shader = Load<CreateShader>("glCreateShader")(type);
        var delete = Load<SingleObject>("glDeleteShader");
        nint text = Marshal.StringToCoTaskMemUTF8(source);
        try
        {
            Load<ShaderSource>("glShaderSource")(shader, 1, &text, null);
            Load<SingleObject>("glCompileShader")(shader);
            Load<ObjectStatus>("glGetShaderiv")(shader, GlCompileStatus, out int compiled);
            if (compiled == 0)
            {
                throw new InvalidOperationException("The native integer blend shader did not compile.");
            }
            return shader;
        }
        catch
        {
            delete(shader);
            throw;
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private void InitializeBlend()
    {
        uint vertex = 0;
        uint fragment = 0;
        var deleteShader = Load<SingleObject>("glDeleteShader");
        try
        {
            _deleteProgram = Load<SingleObject>("glDeleteProgram");
            _useProgram = Load<SingleObject>("glUseProgram");
            _deleteVertexArrays = Load<ObjectNames>("glDeleteVertexArrays");
            _bindVertexArray = Load<SingleObject>("glBindVertexArray");
            _activeTexture = Load<SingleObject>("glActiveTexture");
            _disable = Load<SingleObject>("glDisable");
            _uniformSampler = Load<UniformSampler>("glUniform1i");
            _uniformColour = Load<UniformColour>("glUniform4ui");
            _viewport = Load<Viewport>("glViewport");
            _drawArrays = Load<DrawArrays>("glDrawArrays");
            _textureSubImage = Load<TextureSubImage>("glTexSubImage2D");
            vertex = CompileShader(GlVertexShader, BlendVertexSource);
            fragment = CompileShader(GlFragmentShader, BlendFragmentSource);
            _blendProgram = Load<CreateProgram>("glCreateProgram")();
            var attach = Load<AttachShader>("glAttachShader");
            attach(_blendProgram, vertex);
            attach(_blendProgram, fragment);
            Load<SingleObject>("glLinkProgram")(_blendProgram);
            Load<ObjectStatus>("glGetProgramiv")(_blendProgram, GlLinkStatus, out int linked);
            if (linked == 0)
            {
                throw new InvalidOperationException("The native integer blend program did not link.");
            }
            var location = Load<UniformLocation>("glGetUniformLocation");
            _sourceColourLocation = location(_blendProgram, "sourceColour");
            _destinationLocation = location(_blendProgram, "destination");
            if (_sourceColourLocation < 0 || _destinationLocation < 0)
            {
                throw new InvalidOperationException("The native integer blend uniforms are missing.");
            }
            fixed (uint* array = &_blendVertexArray)
            {
                Load<ObjectNames>("glGenVertexArrays")(1, array);
            }
            BlendSupported = true;
            uint[] backgrounds = [0xFFFFFFFFu, 0x10203040u, 0xDEADBEEFu, 0u];
            foreach (uint colour in new[] { 0u, 0xFF123456u, 0x803F2010u, 0x008090A0u })
            {
                uint[] output = (uint[])backgrounds.Clone();
                if (!TryBlend(2, 2, colour, output))
                {
                    throw new InvalidOperationException("The native integer blend self-test did not complete.");
                }
                for (int pixel = 0; pixel < output.Length; pixel++)
                {
                    if (output[pixel] != AuroraGpu2DEngine.BlendOver(colour, backgrounds[pixel]))
                    {
                        throw new InvalidOperationException("The native integer blend self-test pixels differ.");
                    }
                }
            }
        }
        catch (Exception exception)
        {
            BlendUnavailableReason = exception.Message;
            DeleteBlendPipeline();
            BlendSupported = false;
        }
        finally
        {
            if (vertex != 0)
            {
                deleteShader(vertex);
            }
            if (fragment != 0)
            {
                deleteShader(fragment);
            }
        }
    }

    public bool TryBlend(int width, int height, uint colour, uint[] pixels)
    {
        CheckOwner();
        if (!BlendSupported || width <= 0 || height <= 0 || width > _maximumTextureSize ||
            height > _maximumTextureSize || (long)width * height > pixels.Length)
        {
            return false;
        }
        if (!EnsureStage(width, height))
        {
            return false;
        }
        _activeTexture!(GlTexture0);
        if (_inputTexture == 0)
        {
            fixed (uint* texture = &_inputTexture)
            {
                _generateTextures(1, texture);
            }
            _bindTexture(GlTexture2D, _inputTexture);
            _textureParameter(GlTexture2D, GlTextureMinFilter, GlNearest);
            _textureParameter(GlTexture2D, GlTextureMagFilter, GlNearest);
            _textureImage(GlTexture2D, 0, GlRgba8UnsignedInteger,
                width, height, 0, GlRgbaInteger, GlUnsignedByte, 0);
        }
        _bindTexture(GlTexture2D, _inputTexture);
        _pixelStore(GlUnpackAlignment, 1);
        _pixelStore(GlUnpackRowLength, 0);
        fixed (uint* source = pixels)
        {
            _textureSubImage!(GlTexture2D, 0, 0, 0,
                width, height, GlRgbaInteger, GlUnsignedByte, source);
        }
        _bindFramebuffer(GlFramebuffer, _framebuffer);
        _viewport!(0, 0, width, height);
        _disable!(GlBlend);
        _disable(GlDither);
        _useProgram!(_blendProgram);
        _bindVertexArray!(_blendVertexArray);
        _uniformSampler!(_destinationLocation, 0);
        _uniformColour!(_sourceColourLocation, colour & 0xFFu,
            (colour >> 8) & 0xFFu, (colour >> 16) & 0xFFu, colour >> 24);
        _drawArrays!(GlTriangles, 0, 3);
        _readBuffer(GlColorAttachment0);
        _pixelStore(GlPackAlignment, 1);
        _pixelStore(GlPackRowLength, 0);
        fixed (uint* destination = pixels)
        {
            _readPixels(0, 0, width, height, GlRgbaInteger, GlUnsignedByte, destination);
        }
        _finish();
        return _getError() == GlNoError;
    }

    private void InitializeBatch()
    {
        if (!BlendSupported)
        {
            BatchUnavailableReason = "The integer blend pipeline is unavailable.";
            return;
        }
        uint vertex = 0;
        uint fragment = 0;
        var deleteShader = Load<SingleObject>("glDeleteShader");
        try
        {
            vertex = CompileShader(GlVertexShader, BlendVertexSource);
            fragment = CompileShader(GlFragmentShader, BatchFragmentSource);
            _batchProgram = Load<CreateProgram>("glCreateProgram")();
            var attach = Load<AttachShader>("glAttachShader");
            attach(_batchProgram, vertex);
            attach(_batchProgram, fragment);
            Load<SingleObject>("glLinkProgram")(_batchProgram);
            Load<ObjectStatus>("glGetProgramiv")(_batchProgram, GlLinkStatus, out int linked);
            if (linked == 0)
            {
                throw new InvalidOperationException("The packed integer batch program did not link.");
            }
            var location = Load<UniformLocation>("glGetUniformLocation");
            _batchDestination = location(_batchProgram, "destination");
            _batchColours = location(_batchProgram, "sourceColours[0]");
            _batchMetadata = location(_batchProgram, "segmentMetadata[0]");
            _batchCount = location(_batchProgram, "segmentCount");
            _batchWidth = location(_batchProgram, "atlasWidth");
            if (_batchDestination < 0 || _batchColours < 0 || _batchMetadata < 0 ||
                _batchCount < 0 || _batchWidth < 0)
            {
                throw new InvalidOperationException("The packed batch uniforms are missing.");
            }
            _uniformVectors = Load<UniformVectors>("glUniform4uiv");
            BatchSupported = true;
            uint[] values = [0xffffffffu, 0x10203040u, 0xdeadbeefu];
            AuroraGpuFillOperation[] operations = [new(0, 1, 1, 0x80102030u, true),
                new(1, 2, 1, 0x00123456u, false)];
            uint expected = AuroraGpu2DEngine.BlendOver(operations[0].Colour, values[0]);
            if (!TryBatch(3, values, operations) || values[0] != expected ||
                values[1] != operations[1].Colour || values[2] != operations[1].Colour)
            {
                throw new InvalidOperationException("The packed integer batch self-test failed.");
            }
        }
        catch (Exception exception)
        {
            BatchSupported = false;
            BatchUnavailableReason = exception.Message;
            if (_batchProgram != 0)
            {
                _deleteProgram?.Invoke(_batchProgram);
            }
            _batchProgram = 0;
        }
        finally
        {
            if (vertex != 0)
            {
                deleteShader(vertex);
            }
            if (fragment != 0)
            {
                deleteShader(fragment);
            }
        }
    }

    /// <summary>
    /// Processes captured disjoint rectangles as contiguous independent segments.
    /// Padding belongs only to the private atlas and is never committed by Core.
    /// </summary>
    public bool TryBatch(int pixelCount, uint[] pixels, AuroraGpuFillOperation[] operations)
    {
        CheckOwner();
        if (!BatchSupported || pixelCount <= 0 || pixelCount > pixels.Length ||
            operations.Length == 0 || operations.Length > 128)
        {
            return false;
        }
        uint[] colours = new uint[operations.Length * 4];
        uint[] metadata = new uint[128];
        long end = 0;
        for (int index = 0; index < operations.Length; index++)
        {
            var operation = operations[index];
            if (operation.PixelOffset != end || operation.Width <= 0 || operation.Height <= 0)
            {
                return false;
            }
            end += (long)operation.Width * operation.Height;
            if (end > pixelCount)
            {
                return false;
            }
            metadata[index] = (uint)end | (operation.Blend ? 0x80000000u : 0u);
            uint colour = operation.Colour;
            colours[index * 4] = colour & 255u;
            colours[index * 4 + 1] = (colour >> 8) & 255u;
            colours[index * 4 + 2] = (colour >> 16) & 255u;
            colours[index * 4 + 3] = colour >> 24;
        }
        if (end != pixelCount)
        {
            return false;
        }
        int width = Math.Min(pixelCount, Math.Min(1024, _maximumTextureSize));
        int height = (int)(((long)pixelCount + width - 1) / width);
        if (height > _maximumTextureSize || (long)width * height > int.MaxValue)
        {
            return false;
        }
        uint[] atlas = new uint[width * height];
        pixels.AsSpan(0, pixelCount).CopyTo(atlas);
        if (!EnsureStage(width, height))
        {
            return false;
        }
        _activeTexture!(GlTexture0);
        if (_inputTexture == 0)
        {
            fixed (uint* texture = &_inputTexture)
            {
                _generateTextures(1, texture);
            }
            _bindTexture(GlTexture2D, _inputTexture);
            _textureParameter(GlTexture2D, GlTextureMinFilter, GlNearest);
            _textureParameter(GlTexture2D, GlTextureMagFilter, GlNearest);
            _textureImage(GlTexture2D, 0, GlRgba8UnsignedInteger, width, height, 0,
                GlRgbaInteger, GlUnsignedByte, 0);
        }
        _bindTexture(GlTexture2D, _inputTexture);
        _pixelStore(GlUnpackAlignment, 1);
        _pixelStore(GlUnpackRowLength, 0);
        fixed (uint* source = atlas)
        {
            _textureSubImage!(GlTexture2D, 0, 0, 0, width, height, GlRgbaInteger, GlUnsignedByte, source);
        }
        _bindFramebuffer(GlFramebuffer, _framebuffer);
        _viewport!(0, 0, width, height);
        _disable!(GlBlend);
        _disable(GlDither);
        _useProgram!(_batchProgram);
        _bindVertexArray!(_blendVertexArray);
        _uniformSampler!(_batchDestination, 0);
        _uniformSampler(_batchCount, operations.Length);
        _uniformSampler(_batchWidth, width);
        fixed (uint* values = colours)
        {
            _uniformVectors!(_batchColours, operations.Length, values);
        }
        fixed (uint* values = metadata)
        {
            _uniformVectors!(_batchMetadata, 32, values);
        }
        _drawArrays!(GlTriangles, 0, 3);
        _readBuffer(GlColorAttachment0);
        _pixelStore(GlPackAlignment, 1);
        _pixelStore(GlPackRowLength, 0);
        fixed (uint* destination = atlas)
        {
            _readPixels(0, 0, width, height, GlRgbaInteger, GlUnsignedByte, destination);
        }
        _finish();
        if (_getError() != GlNoError)
        {
            return false;
        }
        atlas.AsSpan(0, pixelCount).CopyTo(pixels);
        return true;
    }

    private void DeleteBlendPipeline()
    {
        if (_blendProgram != 0)
        {
            _useProgram?.Invoke(0);
        }
        if (_blendVertexArray != 0)
        {
            _bindVertexArray?.Invoke(0);
        }
        if (_blendProgram != 0)
        {
            _deleteProgram?.Invoke(_blendProgram);
            _blendProgram = 0;
        }
        if (_blendVertexArray != 0)
        {
            uint array = _blendVertexArray;
            _deleteVertexArrays?.Invoke(1, &array);
            _blendVertexArray = 0;
        }
    }

    private bool EnsureStage(int width, int height)
    {
        if (_width == width && _height == height)
        {
            return true;
        }
        DeleteStage();
        fixed (uint* texture = &_texture)
        {
            _generateTextures(1, texture);
        }
        fixed (uint* framebuffer = &_framebuffer)
        {
            _generateFramebuffers(1, framebuffer);
        }
        _bindTexture(GlTexture2D, _texture);
        _textureParameter(GlTexture2D, GlTextureMinFilter, GlNearest);
        _textureParameter(GlTexture2D, GlTextureMagFilter, GlNearest);
        _textureImage(GlTexture2D, 0, GlRgba8UnsignedInteger,
            width, height, 0, GlRgbaInteger, GlUnsignedByte, 0);
        _bindFramebuffer(GlFramebuffer, _framebuffer);
        _framebufferTexture(GlFramebuffer, GlColorAttachment0, GlTexture2D, _texture, 0);
        if (_framebufferStatus(GlFramebuffer) != GlFramebufferComplete || _getError() != GlNoError)
        {
            DeleteStage();
            return false;
        }
        _width = width;
        _height = height;
        return true;
    }

    private void CheckOwner()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException("The CGL context belongs to its worker thread.");
        }
    }

    private void DeleteStage()
    {
        if (_inputTexture != 0)
        {
            uint texture = _inputTexture;
            _deleteTextures?.Invoke(1, &texture);
            _inputTexture = 0;
        }
        if (_texture != 0)
        {
            uint texture = _texture;
            _deleteTextures?.Invoke(1, &texture);
            _texture = 0;
        }
        if (_framebuffer != 0)
        {
            uint framebuffer = _framebuffer;
            _deleteFramebuffers?.Invoke(1, &framebuffer);
            _framebuffer = 0;
        }
        _width = _height = 0;
    }

    public void Dispose()
    {
        CheckOwner();
        DeleteStage();
        if (_batchProgram != 0)
        {
            _deleteProgram?.Invoke(_batchProgram);
        }
        _batchProgram = 0;
        DeleteBlendPipeline();
        if (_context != 0)
        {
            _setCurrentContext?.Invoke(0);
            _destroyContext?.Invoke(_context);
            _context = 0;
        }
        if (_library != 0)
        {
            NativeLibrary.Free(_library);
            _library = 0;
        }
    }
}
