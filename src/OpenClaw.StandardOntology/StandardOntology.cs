using OpenClaw.Ontology;

namespace OpenClaw.StandardOntology;

/// <summary>
/// GB/T 48000.3-2026 Standard Digitalization Ontology.
/// Provides the 16 core entity types, 34 core object properties,
/// and structural axioms defined by the standard.
///
/// Namespace: http://openclaw.net/ontology/standard#
/// Prefix:    std
/// </summary>
public sealed class StandardOntology
{
    public const string Namespace = "http://openclaw.net/ontology/standard#";
    public const string Prefix = "std";

    /// <summary>
    /// Build the complete GB/T 48000.3 standard ontology.
    /// </summary>
    public OntologyBuilder Build()
    {
        var ob = new OntologyBuilder(Namespace)
            .WithPrefix(Prefix, Namespace)
            .WithHeader(Namespace,
                "GB/T 48000.3-2026 标准数字化本体",
                "基于 GB/T 48000.3-2026《标准数字化 第3部分：本体建模要求》定义的标准数字化核心本体。包含 16 种核心实体类型、34 种核心对象属性和 8 类结构性公理。");

        BuildCoreEntities(ob);
        BuildCoreObjectProperties(ob);
        BuildCoreDataProperties(ob);
        BuildCoreAxioms(ob);

        return ob;
    }

    // ── 附录 B：核心实体类型（16 种）──────────────────────────────────────

    private static void BuildCoreEntities(OntologyBuilder ob)
    {
        // Shorthands
        var C = Prefix;

        // B.1 标准实体
        ob.DeclareClass($"{C}:Standard", "标准实体",
            "标准文件的核心根节点，用于表示具有唯一标识的标准文件实例，聚合元数据、结构和内容及制定过程。",
            hasKey: [$"{C}:standardNumber"]);

        // B.2 标准对象
        ob.DeclareClass($"{C}:StandardizationObject", "标准对象",
            "描述标准化的具体对象或主题。",
            subClassOf: [$"{C}:Entity"]);

        // B.3 元数据
        ob.DeclareClass($"{C}:Metadata", "元数据",
            "标准的管理元数据信息。");

        // B.4 相关方
        ob.DeclareClass($"{C}:RelevantParty", "相关方",
            "参与标准相关活动的角色。",
            subClassOf: [$"{C}:Entity"]);
        ob.DeclareClass($"{C}:Organization", "组织",
            "代表标准活动中企业、协会、委员会等组织实体。",
            subClassOf: [$"{C}:RelevantParty"]);
        ob.DeclareClass($"{C}:Person", "个人",
            "代表标准活动中的个人实体。",
            subClassOf: [$"{C}:RelevantParty"]);

        // B.5 标准分类
        ob.DeclareClass($"{C}:StandardClassification", "标准分类",
            "根据标准领域对标准的分类，如 ICS、CCS 分类。");
        ob.DeclareClass($"{C}:IcsClassification", "国际标准分类",
            "ICS 国际标准分类。",
            subClassOf: [$"{C}:StandardClassification"]);
        ob.DeclareClass($"{C}:CcsClassification", "中国标准分类",
            "CCS 中国标准文献分类。",
            subClassOf: [$"{C}:StandardClassification"]);

        // B.6 要素
        ob.DeclareClass($"{C}:Element", "要素",
            "标准中的要素信息，包含规范性要素和资料性要素。",
            subClassOf: [$"{C}:Entity"]);
        ob.DeclareClass($"{C}:NormativeElement", "规范性要素",
            "标准的规范性要素（范围、术语和定义、符号和缩略语、核心技术要素、管理技术要素等）。",
            subClassOf: [$"{C}:Element"]);
        ob.DeclareClass($"{C}:InformativeElement", "资料性要素",
            "标准的资料性要素（规范性引用文件、参考文献、索引等）。",
            subClassOf: [$"{C}:Element"]);

        // B.7 层次
        ob.DeclareClass($"{C}:Level", "层次",
            "标准结构中的层级概念，用于表示标准内容的组织层次。");
        ob.DeclareClass($"{C}:Chapter", "章",
            "标准中编号为章的逻辑单元。",
            subClassOf: [$"{C}:Level"]);
        ob.DeclareClass($"{C}:Section", "条",
            "章下属的编号为条的单元。",
            subClassOf: [$"{C}:Level"]);
        ob.DeclareClass($"{C}:Paragraph", "段",
            "不带编号的文本段落实体。",
            subClassOf: [$"{C}:Level"]);
        ob.DeclareClass($"{C}:Item", "项",
            "列表中的编号或未编号项。",
            subClassOf: [$"{C}:Level"]);

        // B.8 术语
        ob.DeclareClass($"{C}:Term", "术语",
            "标准中界定的具有特定含义的专业词语。",
            subClassOf: [$"{C}:Entity"]);

        // B.9 信息单元
        ob.DeclareClass($"{C}:InformationUnit", "信息单元",
            "标准内容的最小信息模块。",
            subClassOf: [$"{C}:Entity"]);
        ob.DeclareClass($"{C}:Clause", "条款",
            "信息单元的一种，代表条文性约束。",
            subClassOf: [$"{C}:InformationUnit"]);
        ob.DeclareClass($"{C}:Example", "示例",
            "信息单元的一种，代表示例说明。",
            subClassOf: [$"{C}:InformationUnit"]);
        ob.DeclareClass($"{C}:Note", "注",
            "信息单元的一种，代表注释说明。",
            subClassOf: [$"{C}:InformationUnit"]);
        ob.DeclareClass($"{C}:List", "列表",
            "信息单元的一种，代表枚举或列表。",
            subClassOf: [$"{C}:InformationUnit"]);

        // B.10 信息单元表示形式
        ob.DeclareClass($"{C}:RepresentationForm", "信息单元表示形式",
            "信息单元的不同表现形式（文本、图表、公式、代码等）。");
        ob.DeclareClass($"{C}:TextForm", "文本形式", "以纯文本表示。",
            subClassOf: [$"{C}:RepresentationForm"]);
        ob.DeclareClass($"{C}:FigureForm", "图表形式", "以图形、图像或图表表示。",
            subClassOf: [$"{C}:RepresentationForm"]);
        ob.DeclareClass($"{C}:TableForm", "表格形式", "以表格表示。",
            subClassOf: [$"{C}:RepresentationForm"]);
        ob.DeclareClass($"{C}:FormulaForm", "公式形式", "以数学公式表示。",
            subClassOf: [$"{C}:RepresentationForm"]);
        ob.DeclareClass($"{C}:CodeForm", "代码形式", "以程序代码或伪代码表示。",
            subClassOf: [$"{C}:RepresentationForm"]);

        // B.11 对象
        ob.DeclareClass($"{C}:Object", "对象",
            "标准涉及的人员、设备、材料、软件等实体。",
            subClassOf: [$"{C}:Entity"]);

        // B.12 特性
        ob.DeclareClass($"{C}:Characteristic", "特性",
            "产品/服务/过程的量化属性。",
            subClassOf: [$"{C}:Entity"]);
        ob.DeclareClass($"{C}:StaticCharacteristic", "静态特性",
            "描述对象的静态属性（如尺寸、颜色）。",
            subClassOf: [$"{C}:Characteristic"]);
        ob.DeclareClass($"{C}:FunctionalCharacteristic", "功能特性",
            "描述对象的功能或性能指标（如转换效率、灵敏度）。",
            subClassOf: [$"{C}:Characteristic"]);
        ob.DeclareClass($"{C}:ConstraintCharacteristic", "约束特性",
            "描述对象的约束条件或限定条件（如工作温度、安全等级）。",
            subClassOf: [$"{C}:Characteristic"]);

        // B.13 约束逻辑
        ob.DeclareClass($"{C}:ConstraintLogic", "约束逻辑",
            "具体的数值约束或逻辑约束条件。",
            subClassOf: [$"{C}:Entity"]);

        // B.14 判定
        ob.DeclareClass($"{C}:Determination", "判定",
            "最小可执行规则的抽象。分为合规判定和路径判定。",
            subClassOf: [$"{C}:Entity"]);

        // B.15 外部约束
        ob.DeclareClass($"{C}:ExternalConstraint", "外部约束",
            "与标准相关的外部文件，如法律法规、专利、标准文献、行业数据库等。");
        ob.DeclareClass($"{C}:LawRegulation", "法律法规", "法律或行政法规。",
            subClassOf: [$"{C}:ExternalConstraint"]);
        ob.DeclareClass($"{C}:Patent", "专利", "专利文献。",
            subClassOf: [$"{C}:ExternalConstraint"]);
        ob.DeclareClass($"{C}:ReferenceDocument", "参考文献", "标准中引用的其他标准或文献。",
            subClassOf: [$"{C}:ExternalConstraint"]);

        // B.16 制定阶段
        ob.DeclareClass($"{C}:DevelopmentStage", "制定阶段",
            "标准生命周期中的各个阶段。",
            subClassOf: [$"{C}:Entity"]);

        // B.17 版本
        ob.DeclareClass($"{C}:Version", "版本",
            "标准的特定发布版本，支持版本追溯和差异对比。",
            subClassOf: [$"{C}:Entity"]);

        // B.18 文件编号
        ob.DeclareClass($"{C}:DocumentNumber", "文件编号",
            "标准文件编号，用于支持版本追溯。",
            subClassOf: [$"{C}:Entity"]);

        // 顶层抽象
        ob.DeclareClass($"{C}:Entity", "实体",
            "标准数字化本体的顶层抽象类，所有核心实体类型的基类。");
    }

    // ── 第 7.3.2 节：核心对象属性（34 种）────────────────────────────────

    private static void BuildCoreObjectProperties(OntologyBuilder ob)
    {
        var C = Prefix;
        var Std = $"{C}:Standard";
        var Org = $"{C}:Organization";
        var Cls = $"{C}:StandardClassification";
        var Elem = $"{C}:Element";
        var Lvl = $"{C}:Level";
        var Term = $"{C}:Term";
        var IU = $"{C}:InformationUnit";
        var RF = $"{C}:RepresentationForm";
        var Obj = $"{C}:Object";
        var Charac = $"{C}:Characteristic";
        var ConLog = $"{C}:ConstraintLogic";
        var Det = $"{C}:Determination";
        var ExtCon = $"{C}:ExternalConstraint";
        var DevStage = $"{C}:DevelopmentStage";
        var Pers = $"{C}:Person";
        var RelParty = $"{C}:RelevantParty";

        // 1-2: 替代关系
        ob.DeclareObjectProperty($"{C}:adopts", "采用", "当前标准采用（等同/修改）另一标准。",
            Std, Std);
        ob.DeclareObjectProperty($"{C}:replaces", "代替", "新版本标准代替旧版本标准。",
            Std, Std);

        // 3-4: 引用关系
        ob.DeclareObjectProperty($"{C}:cites", "引用（标准）", "标准规范性引用另一标准。",
            Std, Std);
        ob.DeclareObjectProperty($"{C}:references", "参考", "标准资料性参考另一标准。",
            Std, Std);

        // 5: 部分关系
        ob.DeclareObjectProperty($"{C}:hasPart", "有部分", "标准由多个分部分标准组成。",
            Std, Std);

        // 6-10: 组织关系
        ob.DeclareObjectProperty($"{C}:issuedBy", "发布方", "标准的发布组织。",
            Std, Org, functional: true);
        ob.DeclareObjectProperty($"{C}:proposedBy", "提出方", "标准的提出组织。",
            Std, Org);
        ob.DeclareObjectProperty($"{C}:administeredBy", "归口方", "标准的归口管理组织。",
            Std, Org, functional: true);
        ob.DeclareObjectProperty($"{C}:draftedBy", "起草方", "标准的起草单位或个人。",
            Std, RelParty);
        ob.DeclareObjectProperty($"{C}:publishedBy", "出版方", "标准的出版发行组织。",
            Std, Org);

        // 11: 分类
        ob.DeclareObjectProperty($"{C}:classifiedUnder", "属于分类", "标准归属的分类体系。",
            Std, Cls);

        // 12: 规范对象
        ob.DeclareObjectProperty($"{C}:standardizes", "规范对象", "标准规范的具体对象/主题。",
            Std, $"{C}:StandardizationObject");

        // 13-14: 要素关系
        ob.DeclareObjectProperty($"{C}:hasNormativeElement", "有规范性要素",
            "标准包含的规范性要素。", Std, $"{C}:NormativeElement");
        ob.DeclareObjectProperty($"{C}:hasStructuralElement", "有结构要素",
            "标准包含的结构层次。", Elem, Lvl);

        // 15-16: 层级
        ob.DeclareObjectProperty($"{C}:hasClause", "有条款",
            "要素或结构层次包含的条款。", Elem, $"{C}:Clause");
        ob.DeclareObjectProperty($"{C}:hasSubClause", "有子条款",
            "条款下包含的子条款（构建树形结构）。", $"{C}:Clause", $"{C}:Clause",
            transitive: true);

        // 17-21: 内容关系
        ob.DeclareObjectProperty($"{C}:defines", "界定",
            "标准或要素界定某个术语。", Std, Term);
        ob.DeclareObjectProperty($"{C}:usesTerm", "使用术语",
            "信息单元中提及某个术语。", IU, Term);
        ob.DeclareObjectProperty($"{C}:hasRepresentationForm", "有表示形式",
            "信息单元的表现形式。", IU, RF);
        ob.DeclareObjectProperty($"{C}:hasExample", "有示例",
            "术语或概念具有的示例说明。", Term, $"{C}:Example");
        ob.DeclareObjectProperty($"{C}:hasNote", "有注",
            "术语或条文具有的注释说明。", Term, $"{C}:Note");

        // 22-23: 交叉引用
        ob.DeclareObjectProperty($"{C}:citesStandard", "引用标准",
            "术语或条文引用具体标准。", Term, Std);
        ob.DeclareObjectProperty($"{C}:referencesClause", "指向条款",
            "信息单元指向标准内的章、条、段、项。", IU, Lvl);

        // 24-26: 对象和特性
        ob.DeclareObjectProperty($"{C}:involvesObject", "涉及对象",
            "术语或条文提到的人、设备、材料等。", Term, Obj);
        ob.DeclareObjectProperty($"{C}:specifiesCharacteristic", "规定特性",
            "术语或条文对某特性做出规定。", Term, Charac);
        ob.DeclareObjectProperty($"{C}:hasCharacteristic", "具有特性",
            "对象具有某种可量化的特性或属性。", Obj, Charac);

        // 27-29: 约束
        ob.DeclareObjectProperty($"{C}:imposesConstraint", "施加约束",
            "信息单元或对象附带具体约束条件。", IU, ConLog);
        ob.DeclareObjectProperty($"{C}:constrainsObject", "约束对象",
            "约束逻辑适用的具体对象。", ConLog, Obj);
        ob.DeclareObjectProperty($"{C}:constrainsCharacteristic", "约束特性",
            "约束逻辑适用的特性。", ConLog, Charac);

        // 30-31: 动作和外部
        ob.DeclareObjectProperty($"{C}:describesAction", "描述动作",
            "信息单元规定的执行动作或操作。", IU, Det);
        ob.DeclareObjectProperty($"{C}:referencesExternalResource", "引用外部资源",
            "标准或术语引用的法规、专利、标准文献等。", Std, ExtCon);

        // 32-34: 专利、阶段、包含
        ob.DeclareObjectProperty($"{C}:isRelatedToPatent", "与专利有关",
            "术语或条文与某专利相关的说明。", Term, $"{C}:Patent");
        ob.DeclareObjectProperty($"{C}:hasDevelopmentStage", "处于阶段",
            "标准当前的制定生命周期阶段。", Std, DevStage);
        ob.DeclareObjectProperty($"{C}:includesStandard", "包含标准",
            "制定阶段所包含的标准。", DevStage, Std);

        // Additional: version and document number
        ob.DeclareObjectProperty($"{C}:hasVersion", "有版本",
            "标准具有的发布版本。", Std, $"{C}:Version");
        ob.DeclareObjectProperty($"{C}:hasDocumentNumber", "有文件编号",
            "标准通识的文件编号（用于版本管理）。", Std, $"{C}:DocumentNumber");
    }

    // ── 附录 C：核心数据属性 ─────────────────────────────────────────────

    private static void BuildCoreDataProperties(OntologyBuilder ob)
    {
        var C = Prefix;
        var Std = $"{C}:Standard";
        var T = $"{C}:Term";
        var Charac = $"{C}:Characteristic";
        var ConLog = $"{C}:ConstraintLogic";
        var ExtCon = $"{C}:ExternalConstraint";
        var Lvl = $"{C}:Level";
        var DevStage = $"{C}:DevelopmentStage";
        var Pers = $"{C}:Person";

        // 标准实体核心
        ob.DeclareDatatypeProperty($"{C}:standardNumber", "文件编号",
            "标准的文件编号，符合 GB/T 1.1 规定的编号格式。", Std, "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:standardName", "文件名称",
            "标准的文件名称。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:issuedDate", "发布日期",
            "标准的发布日期。", Std, "xsd:date");
        ob.DeclareDatatypeProperty($"{C}:effectiveDate", "实施日期",
            "标准的实施日期。", Std, "xsd:date");
        ob.DeclareDatatypeProperty($"{C}:purpose", "制定目的",
            "标准的制定目的和范围。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:languageEdition", "语言版本",
            "标准的语种版本。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:standardStatus", "标准状态",
            "标准的生命周期状态（制定中/已发布/已实施/废止/已替代/终止）。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:obligationType", "约束类型",
            "标准的约束力类型（强制性/推荐性）。", Std, "xsd:string");

        // 相关方
        ob.DeclareDatatypeProperty($"{C}:partyName", "名称",
            "组织或个人的名称。", $"{C}:RelevantParty", "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:unifiedSocialCreditCode", "统一社会信用代码",
            "组织的统一社会信用代码。", $"{C}:Organization", "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:location", "所在地",
            "组织所在地或注册地。", $"{C}:Organization", "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:contactInfo", "联系方式",
            "个人的联系方式。", Pers, "xsd:string");

        // 分类
        ob.DeclareDatatypeProperty($"{C}:icsCode", "ICS 代码",
            "国际标准分类代码。", $"{C}:IcsClassification", "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:icsName", "ICS 名称",
            "国际标准分类名称。", $"{C}:IcsClassification", "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:ccsCode", "CCS 代码",
            "中国标准文献分类代码。", $"{C}:CcsClassification", "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:ccsName", "CCS 名称",
            "中国标准文献分类名称。", $"{C}:CcsClassification", "xsd:string");

        // 术语
        ob.DeclareDatatypeProperty($"{C}:termName", "术语名称",
            "术语的标准名称。", T, "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:termDefinition", "术语定义",
            "术语的正式定义文本。", T, "xsd:string");

        // 特性
        ob.DeclareDatatypeProperty($"{C}:characteristicName", "特性名称",
            "特性的标准名称。", Charac, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:characteristicValue", "特性值",
            "特性的取值。", Charac, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:valueType", "值类型",
            "特性的取值类型（数值型、枚举型、布尔型等）。", Charac, "xsd:string");

        // 约束逻辑
        ob.DeclareDatatypeProperty($"{C}:constraintLogicType", "约束逻辑类型",
            "约束的类型（等值约束、范围约束、偏差约束等）。", ConLog, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:maxValue", "最大值",
            "约束的最大值。", ConLog, "xsd:decimal");
        ob.DeclareDatatypeProperty($"{C}:minValue", "最小值",
            "约束的最小值。", ConLog, "xsd:decimal");
        ob.DeclareDatatypeProperty($"{C}:nominalValue", "标称值",
            "约束的标称值。", ConLog, "xsd:decimal");
        ob.DeclareDatatypeProperty($"{C}:toleranceValue", "偏差值",
            "约束的允许偏差值。", ConLog, "xsd:decimal");
        ob.DeclareDatatypeProperty($"{C}:unitOfMeasure", "计量单位",
            "约束的计量单位。", ConLog, "xsd:string");

        // 外部约束
        ob.DeclareDatatypeProperty($"{C}:documentType", "文件类型",
            "外部约束文件的类型。", ExtCon, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:effectivePeriod", "有效期",
            "外部约束文件的有效期。", ExtCon, "xsd:string");

        // 层次
        ob.DeclareDatatypeProperty($"{C}:levelCode", "层次编号",
            "章、条、段、项的结构化编号。", Lvl, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:levelTitle", "层次标题",
            "章、条等层次的标题。", Lvl, "xsd:string");

        // 版本
        ob.DeclareDatatypeProperty($"{C}:versionNumber", "版本号",
            "标准的版本号。", $"{C}:Version", "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:versionStatus", "版本状态",
            "版本的当前状态。", $"{C}:Version", "xsd:string");

        // 阶段
        ob.DeclareDatatypeProperty($"{C}:stageCode", "阶段代码",
            "制定阶段的唯一代码。", DevStage, "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:stageStartDate", "阶段开始日期",
            "制定阶段的开始日期。", DevStage, "xsd:date");
        ob.DeclareDatatypeProperty($"{C}:stageEndDate", "阶段结束日期",
            "制定阶段的结束日期。", DevStage, "xsd:date");
    }

    // ── 第 8.2 节：核心公理 ──────────────────────────────────────────────

    private static void BuildCoreAxioms(OntologyBuilder ob)
    {
        var C = Prefix;
        var Std = $"{C}:Standard";
        var Charac = $"{C}:Characteristic";
        var Lvl = $"{C}:Level";
        var IU = $"{C}:InformationUnit";
        var Elem = $"{C}:Element";

        // 1) 实体类型公理 — 顶级实体不相交
        ob.AssertDisjointClasses(Std, $"{C}:RelevantParty");
        ob.AssertDisjointClasses(Std, Elem);
        ob.AssertDisjointClasses(Std, Lvl);
        ob.AssertDisjointClasses(Std, $"{C}:Term");
        ob.AssertDisjointClasses(Std, IU);
        ob.AssertDisjointClasses(Std, $"{C}:Object");
        ob.AssertDisjointClasses(Std, Charac);
        ob.AssertDisjointClasses(Std, $"{C}:ConstraintLogic");
        ob.AssertDisjointClasses(Std, $"{C}:ExternalConstraint");
        ob.AssertDisjointClasses(Std, $"{C}:DevelopmentStage");

        // 2) 子类层级 — Chapter/Section/Paragraph/Item ⊂ Level
        ob.AssertSubClassOf($"{C}:Chapter", Lvl);
        ob.AssertSubClassOf($"{C}:Section", Lvl);
        ob.AssertSubClassOf($"{C}:Paragraph", Lvl);
        ob.AssertSubClassOf($"{C}:Item", Lvl);

        // 3) 子类层级 — Clause/Example/Note/List ⊂ InformationUnit
        ob.AssertSubClassOf($"{C}:Clause", IU);
        ob.AssertSubClassOf($"{C}:Example", IU);
        ob.AssertSubClassOf($"{C}:Note", IU);
        ob.AssertSubClassOf($"{C}:List", IU);

        // 4) 子类层级 — 特性三分
        ob.AssertSubClassOf($"{C}:StaticCharacteristic", Charac);
        ob.AssertSubClassOf($"{C}:FunctionalCharacteristic", Charac);
        ob.AssertSubClassOf($"{C}:ConstraintCharacteristic", Charac);

        // 5) 子类层级 — 要素二分
        ob.AssertSubClassOf($"{C}:NormativeElement", Elem);
        ob.AssertSubClassOf($"{C}:InformativeElement", Elem);
    }
}