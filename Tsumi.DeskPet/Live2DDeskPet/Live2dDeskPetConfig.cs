using System.ComponentModel;

namespace Tsumi.DeskPet;

public record Live2DDeskPetConfig
{
    public string ModelName { get; set; } = "Mao";

    [Description("把模型目录里没在 model3.json 登记的表情/动作文件也纳入可用列表（很多模型包不登记，不开就用不了它们的表情）")]
    public bool MergeUnregisteredAssets { get; set; } = true;

    [Description("自动隐藏水印：模型自带「水印」表情时，按其参数每帧固定住，水印不再出现")]
    public bool HideWatermark { get; set; } = true;

    [Description("手动指定水印参数固定值，形如 Param191=1;Param71=1；留空则用模型自带水印表情里的值")]
    public string WatermarkParamOverride { get; set; } = "";
}
