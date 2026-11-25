namespace CodeAssist.Cli.Fonts;

public sealed record FontNotice(string Name, string Url, string Sha256);

public static class FontNotices
{
    private const string Root = "https://raw.githubusercontent.com/ryanoasis/nerd-fonts/v3.5.1/";
    public static IReadOnlyList<FontNotice> Sources { get; } = Array.AsReadOnly(new[]
    {
        Official("LICENSE", "1f6ad4edae6479aaace3112ede5279a23284ae54b2a34db66357aef5f64df160"),
        Official("license-audit.md", "c2a3f634fa1e5001912f4f19a42fa86fdef5654691b70000f936089325aa404f"),
        Official("src/glyphs/README.md", "b3382c86b928820ff4135bc4c2cf0da5d871b3c2262b72711a00b21f2daf534a"),
        Official("src/glyphs/codicons/LICENSE.txt", "d6239afa918961b465b07bf7411cbe34ff6685854f58553db7966f4881a0211f"),
        Official("src/glyphs/font-awesome/LICENSE.txt", "de784a808496d49f5f80dd9d1d6e9a51cff712cfbc339e7c25155631ae3cdb5d"),
        Official("src/glyphs/materialdesign/LICENSE", "3bae6305bcaa6d10219b999f65c342034c2d40b1c0e8567fa08605e73ccba829"),
        Official("src/glyphs/octicons/LICENSE", "4f886642f9e8d99eeadd484acdac011c3fde8a338faf52f5ca1408c13b9a9b63"),
        Official("src/glyphs/pomicons/LICENSE", "cb4797a9745683f287c28833db596e9d68ec43082d59359b3a49c504f0f610ce"),
        Official("src/glyphs/powerline-extra/LICENSE", "544307a58e6826c24145b2af1c3578b53e2e21ae0e6c67ab4c6abd544304d26a"),
        Official("src/glyphs/powerline-symbols/LICENSE.txt", "46d5355e4734468135d8e4a30d0916292cc4df2b3ec30aa68c866f82497933be"),
        Official("src/glyphs/weather-icons/OFL.txt", "69cdedc8a4f57a30d68e4f045af8b9cce0ec3b1a6e7eec1ad41737c894f26790"),
        Official("src/glyphs/weather-icons/OFL-FAQ.txt", "8acd621722e71dc89de0087a264497b8964d240eedc53677f6344ab20ce36fd7"),
        Official("src/glyphs/codicons/README.md", "2ab4584b783e38c111f177be7f5a2a5ae2778a1a1fe5ea67e209fd6e6670bb32"),
        Official("src/glyphs/font-awesome/README.md", "9c3cfc2d795121fb0214e71288e32b4beba5d7f350ca8a35b003c618c123aae8"),
        Official("src/glyphs/materialdesign/README.md", "7afc59be7303f6877297db80c1ea8f506bcf4e18290ff85e1bba1584ffa0555b"),
        Official("src/glyphs/pomicons/README.md", "c0de5db022d38bc135ceb3d1d0c892b2a36a0deb2d4df5e728c964e952edeacd"),
        Official("src/glyphs/powerline-extra/README.md", "5c79ad0af0366752aac6a0887126554e74c84cecff9b56e1e411758000d3e8cc"),
        Official("src/glyphs/powerline-symbols/README.md", "c6cde819271276da3f8faa35a1163d902f2a903e9eb375646dd473c0c582cdef"),
        new("devicons/LICENSE", "https://raw.githubusercontent.com/devicons/devicon/v2.17.0/LICENSE", "121194741d4a915b9f5890fdd6dd95121f9b1f816517c792358d72d7c838d664"),
        new("font-awesome-extension/README.md", "https://raw.githubusercontent.com/AndreLZGava/font-awesome-extension/09d80249058ee8018a45da30add4339289dbb466/README.md", "286e163928a042b3a3514a6e2d5cafb00ec86b613c84670f660717387d44be54"),
        new("seti/LICENSE.md", "https://raw.githubusercontent.com/jesseweed/seti-ui/master/LICENSE.md", "d4d2d888c6a0a220a7b0e2d6013cf37539b0e05ef937002b3b33bdb364575b88"),
        new("iec/LICENSE.txt", "https://raw.githubusercontent.com/jloughry/Unicode/805536fb6f93604da985d80e0c6b099f98f8b4d9/LICENSE.txt", "11aef1cdf2d58d822e6857e2574870ccc59d4900052bf5c098e319d750e7ac3d"),
        new("extra/LICENSE.md", "https://raw.githubusercontent.com/source-foundry/Hack/master/LICENSE.md", "1f61bb7c790c59b4b0ecdf304628b94e42ae4c8020094a8c3da73381ab212623")
    });
    private static FontNotice Official(string path, string sha256) => new(path, Root + path, sha256);
}