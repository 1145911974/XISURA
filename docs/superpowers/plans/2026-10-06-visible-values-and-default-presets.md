# XISURA 1.0.72：数值显示与默认预设

用户确认：六模式×三档优化结果作为默认，恢复默认使用新数值；不覆盖已保存个人配置。

1. AdvancedCpuFieldRowV2：输入框与滑条同用“编辑目标，否则硬件回读”；显示回读不生成写入目标，相同文字不重赋值。未知硬件仍显示 —。
2. DefaultPerformancePresets：办公省电/响应，游戏性能/噪音，普通狂飙解除人工性能限制；自定义1/2/3分别对应上述起点。保留静音/极限狂飙内置策略与 CO、PBO、供电保护。
3. DefaultControlPresets：显卡无超频，按用途区分限频；狂飙均衡/性能明确恢复驱动默认。风扇按模式与档位加强散热，不低于既有高温安全基线。灯光保留现有默认。
4. 兼容：旧 GPU 保存配置缺少恢复标记时补 false；显式恢复与指定上限互斥。恢复驱动默认不伪称硬件读回成功。
5. 验证：旧安装 UIA 五轮×七输入框复现；现有默认、重置、保留保存值测试扩充；新增一项显卡恢复兼容测试。客户端全套与 MSI 检查，升级本机、重复刷新验证，再发布 MSI/hash/source 至私人仓库。

功耗/频率是允许上限，无法凭设置保证最低噪音或最高帧率。CPU 起点遵循 [AMD 7745HX规格](https://www.amd.com/en/products/processors/laptop/ryzen/7000-series/amd-ryzen-7-7745hx.html) 的45–75W范围与5.1GHz最高加速；显卡原厂动态加速交由驱动/固件，依据 [NVIDIA说明](https://www.nvidia.com/en-ph/geforce/laptops/40-series/)。本轮不进行负偏移、超频或长时噪音/帧率测量。
