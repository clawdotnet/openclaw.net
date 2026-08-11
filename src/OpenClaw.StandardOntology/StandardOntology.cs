using OpenClaw.Ontology;

namespace OpenClaw.StandardOntology;

/// <summary>
/// GB/T 48000.3-2026 Standard Digitalization Ontology.
///
/// Faithful implementation of the standard's normative model:
///   - Appendix B core entity types B.1–B.18
///   - §6.2.4 hierarchy levels (章/条/段/项 → Level + subclasses)
///   - §6.2.5 content elements (规范性要素/资料性要素)
///   - §7.3.2 — 34 core object properties (with their standard domains/ranges)
///   - §8.2 — core rules (disjointness, subclass, functional, key)
///   - Appendix C — 47 core data properties (C.1–C.47)
///
/// Namespace: http://openclaw.net/ontology/standard#
/// Prefix:    std
///
/// Source of truth: docs/zh-CN/GBT+48000.3-2026.pdf (OCR-verified).
/// </summary>
public sealed class StandardOntology
{
    public const string Namespace = "http://openclaw.net/ontology/standard#";
    public const string Prefix = "std";

    /// <summary>
    /// Build the complete GB/T 48000.3-2026 standard ontology.
    /// </summary>
    public OntologyBuilder Build()
    {
        var ob = new OntologyBuilder(Namespace)
            .WithPrefix(Prefix, Namespace)
            .WithHeader(Namespace,
                "GB/T 48000.3-2026 标准数字化本体",
                "基于 GB/T 48000.3-2026《标准数字化 第3部分：本体建模要求》定义的标准数字化核心本体，" +
                "包含 18 种核心实体类型（附录 B）、34 种核心对象属性（7.3.2）与 47 种核心数据属性（附录 C）。");

        BuildCoreEntities(ob);
        BuildCoreObjectProperties(ob);
        BuildCoreDataProperties(ob);
        BuildCoreAxioms(ob);

        return ob;
    }

    // ── 附录 B：核心实体类型（B.1–B.18）+ 规范性派生类 ──────────────────────

    private static void BuildCoreEntities(OntologyBuilder ob)
    {
        var C = Prefix;

        // B.1 标准实体（hasKey = standardNumber）
        ob.DeclareClass($"{C}:Standard", "标准实体",
            "标准文件的核心根节点，用于表示具有唯一标识的标准文件实例，聚合元数据、结构和内容及制定过程。",
            hasKey: [$"{C}:standardNumber"]);

        // B.2 标准化对象
        ob.DeclareClass($"{C}:StandardizationObject", "标准化对象",
            "描述标准化的具体对象或主题。");

        // B.3 相关方（组织与个人的共同基类）
        ob.DeclareClass($"{C}:Stakeholder", "相关方",
            "参与标准活动的组织和个人（B.3）。");

        // B.4 组织
        ob.DeclareClass($"{C}:Organization", "组织",
            "代表企业、协会、委员会等组织实体。",
            subClassOf: [$"{C}:Stakeholder"]);

        // B.5 个体
        ob.DeclareClass($"{C}:Individual", "个体",
            "代表个人实体。",
            subClassOf: [$"{C}:Stakeholder"]);

        // B.6 领域类别
        ob.DeclareClass($"{C}:DomainCategory", "领域类别",
            "根据标准领域对标准的分类（如 ICS、CCS）。");

        // B.7 国际标准分类（ICS）
        ob.DeclareClass($"{C}:InternationalClassificationofStandard", "国际标准分类",
            "ICS 国际标准分类。",
            subClassOf: [$"{C}:DomainCategory"]);

        // B.8 中国标准分类（CCS）
        ob.DeclareClass($"{C}:ChineseClassificationofStandard", "中国标准分类",
            "CCS 中国标准文献分类。",
            subClassOf: [$"{C}:DomainCategory"]);

        // B.9 内容要素（+ 规范性要素 / 资料性要素）
        ob.DeclareClass($"{C}:ContentElement", "内容要素",
            "标准的规范性/资料性内容要素。");
        ob.DeclareClass($"{C}:NormativeElement", "规范性要素",
            "标准的规范性要素（范围、术语和定义、符号和缩略语、核心技术要素、管理技术要素等）。",
            subClassOf: [$"{C}:ContentElement"]);
        ob.DeclareClass($"{C}:InformativeElement", "资料性要素",
            "标准的资料性要素（规范性引用文件、参考文献、索引等）。",
            subClassOf: [$"{C}:ContentElement"]);

        // B.10 结构要素
        ob.DeclareClass($"{C}:StructuralElement", "结构要素",
            "标准的章、条、段、项等结构要素。");

        // B.11 信息单元（+ 条款 / 示例 / 注 / 列表）
        ob.DeclareClass($"{C}:InformationUnit", "信息单元",
            "标准内容的最小信息模块（条款、示例、注、列表等）。");
        ob.DeclareClass($"{C}:Clause", "条款",
            "信息单元的一种，代表条文性约束。",
            subClassOf: [$"{C}:InformationUnit"]);
        ob.DeclareClass($"{C}:TitledClause", "有标题条",
            "带有标题的条款。",
            subClassOf: [$"{C}:Clause"]);
        ob.DeclareClass($"{C}:Example", "示例",
            "信息单元的一种，代表示例说明。",
            subClassOf: [$"{C}:InformationUnit"]);
        ob.DeclareClass($"{C}:Note", "注",
            "信息单元的一种，代表注释说明。",
            subClassOf: [$"{C}:InformationUnit"]);
        ob.DeclareClass($"{C}:List", "列表",
            "信息单元的一种，代表枚举或列表。",
            subClassOf: [$"{C}:InformationUnit"]);

        // B.12 信息单元表示形式
        ob.DeclareClass($"{C}:InformationForm", "信息单元表示形式",
            "信息单元的不同表现形式（文本、图表、公式、代码等）。");
        ob.DeclareClass($"{C}:TextForm", "文本形式", "以纯文本表示。",
            subClassOf: [$"{C}:InformationForm"]);
        ob.DeclareClass($"{C}:FigureForm", "图表形式", "以图形、图像或图表表示。",
            subClassOf: [$"{C}:InformationForm"]);
        ob.DeclareClass($"{C}:TableForm", "表格形式", "以表格表示。",
            subClassOf: [$"{C}:InformationForm"]);
        ob.DeclareClass($"{C}:FormulaForm", "公式形式", "以数学公式表示。",
            subClassOf: [$"{C}:InformationForm"]);
        ob.DeclareClass($"{C}:CodeForm", "代码形式", "以程序代码或伪代码表示。",
            subClassOf: [$"{C}:InformationForm"]);

        // B.13 对象
        ob.DeclareClass($"{C}:Object", "对象",
            "标准涉及的人员、设备、材料、软件等实体。");

        // B.14 特性（Property）
        ob.DeclareClass($"{C}:Property", "特性",
            "产品/服务/过程的量化或描述属性。");
        ob.DeclareClass($"{C}:DescriptiveProperty", "描述型特性", "描述型特性。",
            subClassOf: [$"{C}:Property"]);
        ob.DeclareClass($"{C}:CapabilityProperty", "能力型特性", "能力型特性。",
            subClassOf: [$"{C}:Property"]);
        ob.DeclareClass($"{C}:ConstraintProperty", "约束型特性", "约束型特性。",
            subClassOf: [$"{C}:Property"]);

        // B.15 约束逻辑（Constraint）
        ob.DeclareClass($"{C}:Constraint", "约束逻辑",
            "具体的数值约束或逻辑约束条件。");

        // B.16 动作类（ActionClass）
        ob.DeclareClass($"{C}:ActionClass", "动作类",
            "描述性动作类别（如测试方法、操作步骤）。");
        ob.DeclareClass($"{C}:Determination", "判定", "最小可执行规则（合规判定/路径判定）。",
            subClassOf: [$"{C}:ActionClass"]);

        // B.17 外部约束（外部资源）
        ob.DeclareClass($"{C}:ExternalResource", "外部约束",
            "与标准相关的外部文件，如法律法规、专利、标准文献、行业数据库等。");
        ob.DeclareClass($"{C}:LawRegulation", "法律法规", "法律或行政法规。",
            subClassOf: [$"{C}:ExternalResource"]);
        ob.DeclareClass($"{C}:Patent", "专利", "专利文献。",
            subClassOf: [$"{C}:ExternalResource"]);
        ob.DeclareClass($"{C}:ReferenceDocument", "参考文献", "标准中引用的其他标准或文献。",
            subClassOf: [$"{C}:ExternalResource"]);

        // B.18 制定程序
        ob.DeclareClass($"{C}:StandardizationProcess", "制定程序",
            "标准生命周期中的各个阶段（预备、立项、起草、征求意见、技术审查、批准发布、出版、复审、废止）。");

        // §6.2.4 层次（章、条、段、项）
        ob.DeclareClass($"{C}:Level", "层次",
            "标准结构中的层级概念，用于表示标准内容的组织层次。");
        ob.DeclareClass($"{C}:Section", "章", "标准中编号为章的逻辑单元。",
            subClassOf: [$"{C}:Level"]);
        ob.DeclareClass($"{C}:Paragraph", "段", "不带编号的文本段落实体。",
            subClassOf: [$"{C}:Level"]);
        ob.DeclareClass($"{C}:Item", "项", "列表中的编号或未编号项。",
            subClassOf: [$"{C}:Level"]);

        // §7.3.2 术语（defines/usesTerm/hasExample/hasNote/isRelatedToPatent 的域/范围）
        ob.DeclareClass($"{C}:Term", "术语",
            "标准中界定的具有特定含义的专业词语。");

        // §6.3 可选扩展类（标准明确允许）
        ob.DeclareClass($"{C}:Version", "版本",
            "标准的特定发布版本，支持版本追溯和差异对比（§6.3.1）。");
        ob.DeclareClass($"{C}:DocumentNumber", "文件编号",
            "标准文件编号，用于支持版本追溯（§6.3.2）。");
    }

    // ── 第 7.3.2 节：核心对象属性（34 种，域/范围按标准表）──────────────────

    private static void BuildCoreObjectProperties(OntologyBuilder ob)
    {
        var C = Prefix;
        var Std = $"{C}:Standard";
        var Org = $"{C}:Organization";
        var Stkh = $"{C}:Stakeholder";
        var DomCat = $"{C}:DomainCategory";
        var StdObj = $"{C}:StandardizationObject";
        var ContEl = $"{C}:ContentElement";
        var Lvl = $"{C}:Level";
        var InfoU = $"{C}:InformationUnit";
        var InfoForm = $"{C}:InformationForm";
        var Term = $"{C}:Term";
        var Clause = $"{C}:Clause";
        var Obj = $"{C}:Object";
        var Prop = $"{C}:Property";
        var Con = $"{C}:Constraint";
        var Action = $"{C}:ActionClass";
        var Ext = $"{C}:ExternalResource";
        var Patent = $"{C}:Patent";
        var Proc = $"{C}:StandardizationProcess";
        var Ver = $"{C}:Version";
        var DocNum = $"{C}:DocumentNumber";

        // 替代关系（1-2）
        ob.DeclareObjectProperty($"{C}:adopts", "采用", "当前标准采用（等同/修改）另一标准。", Std, Std);
        ob.DeclareObjectProperty($"{C}:replaces", "代替", "新版本标准代替旧版本标准（逆属性为 isReplacedBy）。", Std, Std);

        // 引用关系（3-4）
        ob.DeclareObjectProperty($"{C}:cites", "引用（标准）", "标准规范性引用另一标准。", Std, Std);
        ob.DeclareObjectProperty($"{C}:references", "参考", "标准资料性参考另一标准（通常在参考文献中列出）。", Std, Std);

        // 部分关系（5）
        ob.DeclareObjectProperty($"{C}:hasPart", "有部分", "本标准由多个部分系列标准组成，当前标准是其中一个部分。", Std, Std);

        // 组织关系（6-10）
        ob.DeclareObjectProperty($"{C}:issuedBy", "发布于", "标准由某机构正式发布。", Std, Org, functional: true);
        ob.DeclareObjectProperty($"{C}:proposedBy", "提出于", "标准由某单位提出。", Std, Org);
        ob.DeclareObjectProperty($"{C}:administeredBy", "归口于", "标准由某单位归口管理。", Std, Org, functional: true);
        ob.DeclareObjectProperty($"{C}:draftedBy", "起草于", "标准由某起草单位或起草人起草。", Std, Stkh);
        ob.DeclareObjectProperty($"{C}:publishedBy", "出版于", "标准由某出版机构出版。", Std, Org);

        // 分类关系（11）
        ob.DeclareObjectProperty($"{C}:classifiedUnder", "属于领域", "标准按领域分类（如 ICS、CCS）。", Std, DomCat);

        // 规范对象（12）
        ob.DeclareObjectProperty($"{C}:standardizes", "规范对象", "本标准所规范的主题对象。", Std, StdObj);

        // 要素关系（13-14）
        ob.DeclareObjectProperty($"{C}:hasNormativeElement", "包含要素", "标准包含规范性/资料性要素（如术语、范围）。", Std, ContEl);
        ob.DeclareObjectProperty($"{C}:hasStructuralElement", "包含层次", "标准或其内部结构包含章、条、段等层级结构。", Std, Lvl);

        // 层次关系（15-16）
        ob.DeclareObjectProperty($"{C}:hasClause", "包含条款", "要素或结构层次包含了一个具体的条款。", ContEl, Clause);
        ob.DeclareObjectProperty($"{C}:hasSubClause", "包含子条", "表示条包含子条，用于构建层次结构。", Clause, Clause, transitive: true);

        // 内容关系（17-21）
        ob.DeclareObjectProperty($"{C}:defines", "界定", "标准或要素界定了某个术语。", Std, Term);
        ob.DeclareObjectProperty($"{C}:usesTerm", "提及术语", "信息单元提及了某个术语。", InfoU, Term);
        ob.DeclareObjectProperty($"{C}:hasRepresentationForm", "具有表述形式", "信息单元（条款等）有内容形式（条文、图、表等）。", InfoU, InfoForm);
        ob.DeclareObjectProperty($"{C}:hasExample", "有示例", "条款关联示例。", Clause, $"{C}:Example");
        ob.DeclareObjectProperty($"{C}:hasNote", "有注", "条款关联注。", Clause, $"{C}:Note");

        // 交叉引用（22-23）
        ob.DeclareObjectProperty($"{C}:citesStandard", "引用标准（条款）", "条款内容中引用了某个标准。", Clause, Std);
        ob.DeclareObjectProperty($"{C}:referencesClause", "引用章条", "信息单元引用本标准内的章、条、段、项。", InfoU, Lvl);

        // 对象与特性（24-26）
        ob.DeclareObjectProperty($"{C}:involvesObject", "涉及对象", "条款提及了人、设备、材料等对象。", Clause, Obj);
        ob.DeclareObjectProperty($"{C}:specifiesCharacteristic", "规定特性", "条款对某个特性的参数/指标做出了规定。", Clause, Prop);
        ob.DeclareObjectProperty($"{C}:hasCharacteristic", "具有特性", "对象具有某种可量化或描述的特性。", Obj, Prop);

        // 约束（27-29）
        ob.DeclareObjectProperty($"{C}:imposesConstraint", "施加约束", "信息单元或特性包含具体的约束条件（如阈值）。", InfoU, Con);
        ob.DeclareObjectProperty($"{C}:constrainsObject", "约束对象", "约束条件应用于某个具体对象。", Con, Obj);
        ob.DeclareObjectProperty($"{C}:constrainsCharacteristic", "约束特性", "约束条件应用于对象的某个特性。", Con, Prop);

        // 动作与外部（30-32）
        ob.DeclareObjectProperty($"{C}:describesAction", "描述动作", "信息单元规定的执行动作或操作步骤。", InfoU, Action);
        ob.DeclareObjectProperty($"{C}:referencesExternalResource", "引用外部资源", "标准或条款引用了法规、专利、标准文献等。", Std, Ext);
        ob.DeclareObjectProperty($"{C}:isRelatedToPatent", "与专利有关", "条款的技术内容与某项专利有关。", Clause, Patent);

        // 阶段关系（33-34）
        ob.DeclareObjectProperty($"{C}:hasDevelopmentStage", "处于阶段", "标准当前所处的生命周期阶段。", Std, Proc);
        ob.DeclareObjectProperty($"{C}:includesStandard", "包含标准", "制定程序阶段所包含的标准（处于阶段属性的逆向属性）。", Proc, Std);

        // 可选扩展（§6.3）：版本与文件编号
        ob.DeclareObjectProperty($"{C}:hasVersion", "有版本", "标准具有的发布版本。", Std, Ver);
        ob.DeclareObjectProperty($"{C}:hasDocumentNumber", "有文件编号", "标准通识的文件编号（用于版本管理）。", Std, DocNum);
    }

    // ── 附录 C：核心数据属性（C.1–C.47，域/范围/枚举按标准）─────────────────

    private static void BuildCoreDataProperties(OntologyBuilder ob)
    {
        var C = Prefix;
        var Std = $"{C}:Standard";
        var StdObj = $"{C}:StandardizationObject";
        var Org = $"{C}:Organization";
        var Ind = $"{C}:Individual";
        var ICS = $"{C}:InternationalClassificationofStandard";
        var CCS = $"{C}:ChineseClassificationofStandard";
        var ContEl = $"{C}:ContentElement";
        var Sec = $"{C}:Section";
        var Clause = $"{C}:Clause";
        var Titled = $"{C}:TitledClause";
        var InfoU = $"{C}:InformationUnit";
        var Obj = $"{C}:Object";
        var Prop = $"{C}:Property";
        var Con = $"{C}:Constraint";
        var Ext = $"{C}:ExternalResource";
        var Proc = $"{C}:StandardizationProcess";

        // C.1–C.8 标准实体
        ob.DeclareDatatypeProperty($"{C}:purpose", "编制目的",
            "说明该标准的制定目标，如“促进技术统一”“保障安全性”等。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:languageVersion", "语言版本",
            "标准发布的语言版本（枚举：中文版本、英文版本、多语种）。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:status", "标准状态",
            "标识标准的状态（枚举：草案、现行、废止、修订中）。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:constraintType", "约束类型",
            "规定标准的约束级别（枚举：强制性、推荐性）。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:documentName", "文件名称",
            "标准的完整名称，如“电动自行车安全技术规范”。", Std, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:standardNumber", "文件编号",
            "符合 GB/T 1.1 的编号格式，如“GB/T 12345—2023”（格式：标准代号+顺序号+发布年份）。",
            Std, "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:issuedDate", "发布日期",
            "标准的官方发布日期（GB/T 7408，格式 YYYY-MM-DD）。", Std, "xsd:date");
        ob.DeclareDatatypeProperty($"{C}:effectiveDate", "实施日期",
            "标准开始实施的日期，可能与发布日期不同（格式 YYYY-MM-DD）。", Std, "xsd:date");

        // C.9–C.10 标准化对象
        ob.DeclareDatatypeProperty($"{C}:subjectName", "主题名称",
            "标识并描述被标准化的事物的主题名称。", StdObj, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:industrialSector", "所属行业",
            "指明标准化对象所服务的特定行业分类或领域（行业分类见 GB/T 4754）。", StdObj, "xsd:string");

        // C.11–C.13 组织
        ob.DeclareDatatypeProperty($"{C}:orgName", "名称",
            "标识机构的法定或通用名称。", Org, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:creditCode", "统一信用代码",
            "由 18 位数字/字母组成的唯一标识符（正则 ^[A-Z0-9]{18}$）。", Org, "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:orgLocation", "所在地",
            "机构的实际办公或注册地址（格式：省/市）。", Org, "xsd:string");

        // C.14–C.17 个体
        ob.DeclareDatatypeProperty($"{C}:personName", "姓名",
            "个人法定姓名。", Ind, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:affiliation", "所属单位",
            "个人所属的机构或组织名称。", Ind, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:phone", "联系电话",
            "个人的联系电话号码（格式：+86-区号-号码）。", Ind, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:address", "联系地址",
            "个人的联系地址。", Ind, "xsd:string");

        // C.18–C.19 ICS
        ob.DeclareDatatypeProperty($"{C}:ICS_code", "ICS分类代码",
            "国际标准分类（ICS）代码。", ICS, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:ICS_name", "ICS分类名称",
            "国际标准分类（ICS）名称。", ICS, "xsd:string");

        // C.20–C.21 CCS
        ob.DeclareDatatypeProperty($"{C}:CCS_code", "CCS分类代码",
            "中国标准文献分类（CCS）代码。", CCS, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:CCS_name", "CCS分类名称",
            "中国标准文献分类（CCS）名称。", CCS, "xsd:string");

        // C.22–C.23 内容要素
        ob.DeclareDatatypeProperty($"{C}:elementStatus", "要素状态",
            "标识要素的存在状态（枚举：必备、可选）。", ContEl, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:scopeOfEffect", "作用范围",
            "规定要素的适用范围。", ContEl, "xsd:string");

        // C.24–C.25 章（Section）
        ob.DeclareDatatypeProperty($"{C}:sectionNumber", "章编号",
            "章的层级编号（如“4”）。", Sec, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:sectionTitle", "章标题",
            "章的标题（如“质量管理体系”）。", Sec, "xsd:string");

        // C.26–C.27 条（Clause）/ 有标题条
        ob.DeclareDatatypeProperty($"{C}:clauseNumber", "条编号",
            "条的层级编号（如“4.1”）。", Clause, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:clauseTitle", "条标题",
            "有标题条的标题。", Titled, "xsd:string");

        // C.28–C.29 信息单元
        // 注：标准原文标识符拼写为 uniqueldentifier（疑似笔误），保留以保与标准发布 IRI 的互操作性。
        ob.DeclareDatatypeProperty($"{C}:uniqueldentifier", "唯一标识符",
            "信息单元的唯一编码（如“IU-001”）。", InfoU, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:contentDescription", "内容描述",
            "信息单元的详细描述文本。", InfoU, "xsd:string");

        // C.30–C.31 条款（Clause）
        ob.DeclareDatatypeProperty($"{C}:clauseType", "条款类型",
            "标识条款类型（枚举：要求型、推荐型、指示型、允许型、陈述型）。", Clause, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:constraintType", "约束类型",
            "规定条款的约束级别（枚举：强制、推荐、描述）。", Clause, "xsd:string");

        // C.32–C.33 对象
        ob.DeclareDatatypeProperty($"{C}:objectName", "对象名称",
            "对象的名称（如“锂电池”“电机控制器”）。", Obj, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:objectCategory", "对象类别",
            "标识对象类别（如“设备”“材料”）。", Obj, "xsd:string");

        // C.34–C.36 特性（Property）
        ob.DeclareDatatypeProperty($"{C}:propertyName", "特性名称",
            "特性的名称（如“电压”“功率”）。", Prop, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:propertyValue", "特性值",
            "特性的具体值（如“48V”“500W”）。", Prop, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:propertyType", "特性类型",
            "标识特性类型（枚举：描述型、能力型、约束型）。", Prop, "xsd:string");

        // C.37–C.41 约束逻辑（Constraint）
        ob.DeclareDatatypeProperty($"{C}:constraintType", "约束类型",
            "约束的形式（枚举：数值区间、枚举值、逻辑表达式）。", Con, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:maxValue", "最大值",
            "数值约束的最大允许值（如“100W”）。", Con, "xsd:decimal");
        ob.DeclareDatatypeProperty($"{C}:minValue", "最小值",
            "数值约束的最小允许值（如“80W”）。", Con, "xsd:decimal");
        ob.DeclareDatatypeProperty($"{C}:thresholdRange", "阈值范围",
            "数值的有效范围（如“80-100W”，格式：最小值-最大值）。", Con, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:unit", "测量单位",
            "数值的单位（如“V”“W”）。", Con, "xsd:string");

        // C.42–C.44 外部约束（外部资源）
        ob.DeclareDatatypeProperty($"{C}:fileType", "文件类型",
            "外部文件的分类（枚举：法规、专利、文献、公共数据库）。", Ext, "xsd:string");
        ob.DeclareDatatypeProperty($"{C}:effectiveTime", "生效时间",
            "文件的生效日期（格式 YYYY-MM-DD）。", Ext, "xsd:date");
        ob.DeclareDatatypeProperty($"{C}:responsibleParty", "责任主体",
            "文件的责任主体（如“国家标准化管理委员会”）。", Ext, "xsd:string");

        // C.45–C.47 制定程序
        ob.DeclareDatatypeProperty($"{C}:stageCode", "阶段代码",
            "用于唯一标识和区分标准制定过程中的不同阶段。", Proc, "xsd:string", functional: true);
        ob.DeclareDatatypeProperty($"{C}:startDate", "开始日期",
            "某个阶段活动开始的具体时间点（格式 YYYY-MM-DD）。", Proc, "xsd:date");
        ob.DeclareDatatypeProperty($"{C}:endDate", "结束日期",
            "某个阶段活动完成的具体时间点（格式 YYYY-MM-DD）。", Proc, "xsd:date");
    }

    // ── 第 8.2 节：核心规则 ──────────────────────────────────────────────

    private static void BuildCoreAxioms(OntologyBuilder ob)
    {
        var C = Prefix;
        var Std = $"{C}:Standard";
        var Stkh = $"{C}:Stakeholder";
        var DomCat = $"{C}:DomainCategory";
        var ContEl = $"{C}:ContentElement";
        var StructEl = $"{C}:StructuralElement";
        var InfoU = $"{C}:InformationUnit";
        var Obj = $"{C}:Object";
        var Prop = $"{C}:Property";
        var Con = $"{C}:Constraint";
        var Action = $"{C}:ActionClass";
        var Ext = $"{C}:ExternalResource";
        var Proc = $"{C}:StandardizationProcess";
        var Lvl = $"{C}:Level";
        var Term = $"{C}:Term";
        var StdObj = $"{C}:StandardizationObject";

        // 8.2 a) 实体类型不相交规则：标准实体与主要实体类型互斥
        foreach (var other in new[]
        {
            Stkh, DomCat, ContEl, StructEl, InfoU, Obj, Prop, Con, Action, Ext, Proc, Lvl, Term, StdObj
        })
        {
            ob.AssertDisjointClasses(Std, other);
        }

        // 8.2 a) 示例：规范性要素与资料性要素互斥
        ob.AssertDisjointClasses($"{C}:NormativeElement", $"{C}:InformativeElement");

        // 8.2 a) 组织与个体互斥（8.2 示例“规范性要素与资料性要素互斥”同属互斥规则）
        ob.AssertDisjointClasses($"{C}:Organization", $"{C}:Individual");

        // 8.2 c) 层级结构约束：章/段/项 ⊂ 层次
        ob.AssertSubClassOf($"{C}:Section", Lvl);
        ob.AssertSubClassOf($"{C}:Paragraph", Lvl);
        ob.AssertSubClassOf($"{C}:Item", Lvl);

        // 8.2 子类层级：条款/示例/注/列表 ⊂ 信息单元
        ob.AssertSubClassOf($"{C}:Clause", InfoU);
        ob.AssertSubClassOf($"{C}:TitledClause", $"{C}:Clause");
        ob.AssertSubClassOf($"{C}:Example", InfoU);
        ob.AssertSubClassOf($"{C}:Note", InfoU);
        ob.AssertSubClassOf($"{C}:List", InfoU);

        // 规范性要素/资料性要素 ⊂ 内容要素
        ob.AssertSubClassOf($"{C}:NormativeElement", ContEl);
        ob.AssertSubClassOf($"{C}:InformativeElement", ContEl);

        // 特性三分 ⊂ 特性
        ob.AssertSubClassOf($"{C}:DescriptiveProperty", Prop);
        ob.AssertSubClassOf($"{C}:CapabilityProperty", Prop);
        ob.AssertSubClassOf($"{C}:ConstraintProperty", Prop);

        // ICS / CCS ⊂ 领域类别
        ob.AssertSubClassOf($"{C}:InternationalClassificationofStandard", DomCat);
        ob.AssertSubClassOf($"{C}:ChineseClassificationofStandard", DomCat);

        // 组织 / 个体 ⊂ 相关方
        ob.AssertSubClassOf($"{C}:Organization", Stkh);
        ob.AssertSubClassOf($"{C}:Individual", Stkh);

        // 外部资源子类
        ob.AssertSubClassOf($"{C}:LawRegulation", Ext);
        ob.AssertSubClassOf($"{C}:Patent", Ext);
        ob.AssertSubClassOf($"{C}:ReferenceDocument", Ext);

        // 动作类子类
        ob.AssertSubClassOf($"{C}:Determination", Action);

        // 信息单元表示形式子类
        ob.AssertSubClassOf($"{C}:TextForm", $"{C}:InformationForm");
        ob.AssertSubClassOf($"{C}:FigureForm", $"{C}:InformationForm");
        ob.AssertSubClassOf($"{C}:TableForm", $"{C}:InformationForm");
        ob.AssertSubClassOf($"{C}:FormulaForm", $"{C}:InformationForm");
        ob.AssertSubClassOf($"{C}:CodeForm", $"{C}:InformationForm");

        // 8.2 b) 唯一值约束 / 8.2 a) 全局唯一标识：已由实体类型 hasKey 与 functional 属性表达
        //   - Standard.hasKey = standardNumber（见 BuildCoreEntities）
        //   - issuedBy / administeredBy / creditCode / stageCode 已声明为 functional
        // 8.2 b) 日期有效性（实施日期 ≥ 发布日期）：属数据层规则，由 SHACL/应用层校验，不在 OWL 公理层表达。
    }
}
