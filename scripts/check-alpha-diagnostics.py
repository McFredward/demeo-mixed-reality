#!/usr/bin/env python3
"""Gate the owned Battlemarked final-output diagnostic shader bundle."""
from pathlib import Path
import UnityPy

ROOT = Path(__file__).resolve().parent.parent

def main():
    path=ROOT/'assets/alphadiagnostics.bundle'
    objects=list(UnityPy.load(str(path)).objects)
    assert sorted(o.type.name for o in objects)==['AssetBundle','Shader']
    bundle=next(o for o in objects if o.type.name=='AssetBundle').read_typetree()
    shader=next(o for o in objects if o.type.name=='Shader')
    directory=bundle['m_Container']
    assert len(directory)==1 and directory[0][0]=='assets/alphadiagnostics.shader'
    assert directory[0][1]['asset']=={'m_FileID':0,'m_PathID':shader.path_id}
    assert not bundle['m_Dependencies'] and not bundle['m_IsStreamedSceneAssetBundle']
    data=shader.read_typetree(); parsed=data['m_ParsedForm']
    assert parsed['m_Name']=='Hidden/Demeo/AlphaDiagnostics' and data['platforms']==[4]
    assert 'STEREO_INSTANCING_ON' in parsed['m_KeywordNames'] and len(parsed['m_SubShaders'])==1
    assert '_LINEAR_TO_SRGB_CONVERSION' in parsed['m_KeywordNames'], 'Rebuild diagnostic bundle after final-blit colour conversion changes'
    passes=parsed['m_SubShaders'][0]['m_Passes']; assert len(passes)==1
    state=passes[0]['m_State']; assert state['m_Name']=='ALPHA_TEST_STRIP'
    assert all(state[k]['val']==v for k,v in {'zWrite':0,'zTest':8,'culling':0,'alphaToMask':0,'stencilWriteMask':0}.items())
    blend=state['rtBlend0']
    assert all(blend[k]['val']==v for k,v in {'srcBlend':1,'destBlend':0,'srcBlendAlpha':1,'destBlendAlpha':0,'colMask':15}.items())
    print('Verified Battlemarked diagnostic bundle: owned D3D11 stereo shader, RGBA replace, no depth/stencil writes')

if __name__=='__main__': main()
