from pathlib import Path
p=Path('src/WGTun.Native/src/main.rs')
s=p.read_text(encoding='utf-8').replace('mod bridge;', 'mod bridge;\nmod widgets;\nmod tray;')
s=s.replace('    capture_frame: u8,','    capture_frame: u8,\n    tray: Option<tray::Tray>,\n    quitting: bool,')
s=s.replace('            capture_frame: 0,','            capture_frame: 0,\n            tray: if offline { None } else { tray::Tray::new(cc.egui_ctx.clone()) },\n            quitting: false,')
s=s.replace('"closeToTray":false', '"closeToTray":true')
s=s.replace('    fn connection(&mut self, ui: &mut egui::Ui) {', '    fn old_connection(&mut self, ui: &mut egui::Ui) {',1)
a=s.index('    fn old_connection(');b=s.index('    fn profiles(',a)
s=s[:a]+'''    fn connection(&mut self, ui: &mut egui::Ui) {
        title(ui,"Senin ağın, senin kuralların","Bağlantın kontrolünde.");
        let profile=n(&self.settings,"profile");
        if let Some(selected)=widgets::segments(ui,"method",&["WireGuard","DPI aşma"],if profile==2{1}else{0}) {
            self.settings["profile"]=json!(if selected==1{2}else{0});self.save();
        }
        ui.add_space(12.);
        card(ui,|ui|{
            ui.set_min_width(ui.available_width());
            ui.vertical_centered(|ui|{
                ui.add_space(10.);
                let(r,response)=ui.allocate_exact_size(Vec2::splat(92.),egui::Sense::click());
                let c=r.center();let p=ui.painter();
                p.circle_stroke(c,44.,Stroke::new(1.,LINE));p.circle_filled(c,34.,SOFT);p.circle_stroke(c,34.,Stroke::new(1.,ACCENT));
                let points:Vec<_>=(0..=40).map(|i|{let a=0.65+i as f32/40.*(std::f32::consts::TAU-1.3);c+Vec2::new(a.sin()*13.,-a.cos()*13.)}).collect();
                p.add(egui::Shape::line(points,Stroke::new(2.,ACCENT)));
                p.line_segment([c+Vec2::new(0.,-18.),c+Vec2::new(0.,-1.)],Stroke::new(2.,ACCENT));
                if response.on_hover_text("Bağlan / bağlantıyı kes").clicked() && self.pending==0 {
                    if self.connected {self.call("disconnect",vec![],None);}
                    else if !b(&self.scan,"serviceInstalled") && !self.offline {self.page="onboarding".into();}
                    else if profile==0 && !b(&self.scan,"hasPreparedWarpProfile") {self.connect_after=true;self.page="profiles".into();}
                    else {self.call("connect",vec![],Some(self.settings.clone()));}
                }
                ui.add_space(6.);
                ui.label(RichText::new(if self.pending>0{"İşlem sürüyor…"}else if self.connected{"Motor çalışıyor."}else{"Bağlanmaya hazırsın."}).size(21.).strong());
                small(ui,if self.connected{"Bağlantı motoru etkin"}else{"Bağlantı kapalı"});
                ui.add_space(12.);
            });
            ui.separator();ui.add_space(5.);
            ui.horizontal(|ui|{
                widgets::icon(ui,if profile==0{"cloud"}else{"profiles"},if profile==0{Color32::from_rgb(225,139,53)}else{ACCENT},32.);
                ui.vertical(|ui|{ui.label(RichText::new(match profile{1=>"Özel WireGuard Profili",2=>"DPI aşma",_=>"Cloudflare WARP"}).size(13.).strong());small(ui,"Seçili profil");});
                ui.with_layout(egui::Layout::right_to_left(egui::Align::Center),|ui|{if button(ui,"Değiştir",false){self.page="profiles".into();}});
            });
        });
        ui.add_space(15.);ui.label(RichText::new("Trafik yönlendirme").size(14.).strong());ui.add_space(5.);
        if let Some(scope)=widgets::segments(ui,"scope",&["Tüm trafik","Yalnızca seçilenler","Seçilenler hariç"],n(&self.settings,"scope") as usize){self.settings["scope"]=json!(scope);self.save();}
        ui.add_space(9.);
        if n(&self.settings,"scope")==0{return;}
        let rules=self.settings["rules"].as_array().cloned().unwrap_or_default();
        let mut changes=Vec::new();let mut edit=None;let mut add=false;
        for row in 0..(rules.len()+1).div_ceil(2){
            ui.columns(2,|columns|{for(col,ui)in columns.iter_mut().enumerate(){let i=row*2+col;if i>rules.len(){continue;}card(ui,|ui|{
                ui.set_min_width(ui.available_width());ui.set_min_height(43.);
                if i==rules.len(){if ui.add_sized([ui.available_width(),43.],egui::Button::new(RichText::new("+  Kural ekle").size(12.).color(SUB)).fill(PANEL).stroke(Stroke::NONE)).clicked(){add=true;}return;}
                let rule=&rules[i];
                ui.horizontal(|ui|{
                    let(r,_)=ui.allocate_exact_size(Vec2::splat(30.),egui::Sense::hover());ui.painter().rect_filled(r,8.,BG);
                    let name=s(rule,"name");let label=if name.is_empty(){s(rule,"value")}else{name};let initial=if n(rule,"type")<5{"IP".to_owned()}else{label.chars().next().unwrap_or('A').to_uppercase().to_string()};
                    ui.painter().text(r.center(),egui::Align2::CENTER_CENTER,initial,egui::FontId::proportional(12.),INK);
                    let controls=49.;let label_width=(ui.available_width()-controls-8.).max(30.);
                    ui.allocate_ui_with_layout(Vec2::new(label_width,36.),egui::Layout::top_down(egui::Align::LEFT),|ui|{ui.add(egui::Label::new(RichText::new(label).size(12.).strong()).truncate()).on_hover_text(s(rule,"value"));small(ui,types(n(rule,"type")));});
                    let mut enabled=b(rule,"enabled");if widgets::toggle(ui,&mut enabled).changed(){changes.push((i,enabled));}
                    let(r,response)=ui.allocate_exact_size(Vec2::splat(18.),egui::Sense::click());widgets::draw_icon(ui.painter(),r.center(),"pencil",SUB);if response.on_hover_text("Kuralı düzenle").clicked(){edit=Some(i);}
                });
            });}});ui.add_space(1.);
        }
        if let Some(i)=edit{self.rule=Some(rules[i].clone());self.rule_index=Some(i);}
        if add{self.rule=Some(json!({"id":uuid::Uuid::new_v4().to_string(),"type":1,"value":"","name":"","enabled":true}));self.rule_index=None;}
        if !changes.is_empty(){for(i,value)in changes{self.settings["rules"][i]["enabled"]=json!(value);}self.save();}
    }
'''+s[b:]
# Replace sidebar brand and nav with properly supported drawn symbols.
a=s.index('                ui.horizontal(|ui| {',s.index('egui::SidePanel::left'));b=s.index('                ui.with_layout(egui::Layout::bottom_up',a)
s=s[:a]+'''                let mut word=egui::text::LayoutJob::default();
                word.append("wg",0.,egui::TextFormat{font_id:egui::FontId::proportional(24.),color:INK,..Default::default()});
                word.append("tun",0.,egui::TextFormat{font_id:egui::FontId::proportional(24.),color:ACCENT,..Default::default()});
                ui.horizontal(|ui|{ui.label(word);widgets::icon(ui,"arrow",ACCENT,18.);});
                ui.add_space(22.);
                for(id,label)in [("connection","Bağlantı"),("profiles","Profiller"),("settings","Ayarlar")]{if widgets::nav(ui,id,label,self.page==id){self.page=id.into();}ui.add_space(4.);}
'''+s[b:]
s=s.replace('        self.replies();','''        self.replies();
        loop {
            let action=self.tray.as_ref().and_then(|tray|tray.action());
            match action {
                Some(tray::Action::Open)=>{ctx.send_viewport_cmd(egui::ViewportCommand::Visible(true));ctx.send_viewport_cmd(egui::ViewportCommand::Focus);},
                Some(tray::Action::Disconnect)=>{if self.pending==0{self.call("disconnect",vec![],None);}},
                Some(tray::Action::Exit)=>{self.quitting=true;ctx.send_viewport_cmd(egui::ViewportCommand::Close);},
                None=>break,
            }
        }
        if !self.offline && !self.quitting && self.tray.is_some() && b(&self.settings,"closeToTray") && ctx.input(|i|i.viewport().close_requested()) {
            ctx.send_viewport_cmd(egui::ViewportCommand::CancelClose);ctx.send_viewport_cmd(egui::ViewportCommand::Visible(false));
        }
''',1)
# Smoke fixture is opt-in and cannot touch real stored settings.
s=s.replace('        Self {\n            bridge,','''        let fixture=args.iter().any(|a|a=="--fixture") && offline;
        let mut result=Self {
            bridge,''',1)
needle='''            quitting: false,
        }
    }'''
s=s.replace(needle,'''            quitting: false,
        };
        if fixture {
            result.settings["rules"]=json!([
                {"id":"fixture-discord","type":5,"name":"Discord","value":"Discord.exe","enabled":true},
                {"id":"fixture-edge","type":5,"name":"Microsoft Edge","value":"msedge.exe","enabled":true},
                {"id":"fixture-network","type":3,"name":"Yerel ağ","value":"192.168.0.0/16","enabled":false}
            ]);
        }
        if result.page=="rule" {result.page="connection".into();result.rule=Some(json!({"id":"fixture-rule","type":1,"name":"","value":"","enabled":true}));}
        result
    }''',1)
# Only automatic background launches are hidden. Smoke/captures remain non-activating, so they can render.
s=s.replace('    let options = eframe::NativeOptions {','    let hidden=args.iter().any(|a|a=="--background"||a=="--startup") && !args.iter().any(|a|a=="--smoke-no-network"||a.starts_with("--capture="));\n    let options = eframe::NativeOptions {')
s=s.replace('            .with_active(!background),','            .with_active(!background)\n            .with_visible(!hidden),')
p.write_text(s,encoding='utf-8')
